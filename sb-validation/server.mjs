import express from 'express';
import { ManagedIdentityCredential } from '@azure/identity';
import { ServiceBusClient } from '@azure/service-bus';
import { randomUUID } from 'node:crypto';

const app = express();
const namespace = process.env.SERVICE_BUS_NAMESPACE;
const queue = process.env.SERVICE_BUS_ENTITY_NAME || 'validation';
const identityClientId = process.env.Identity_embrapp3val_uami_161126_ClientId;
const credential = identityClientId
  ? new ManagedIdentityCredential(identityClientId)
  : new ManagedIdentityCredential();
const log = (event, details = {}) => console.log(JSON.stringify({ time: new Date().toISOString(), event, ...details }));
const logError = (event, error, details = {}) => console.error(JSON.stringify({ time: new Date().toISOString(), event, ...details, error: { name: error.name, code: error.code, message: error.message } }));
const client = namespace ? new ServiceBusClient(namespace, credential) : null;
app.use((req, res, next) => {
  const started = Date.now();
  res.on('finish', () => log('http.request', { method: req.method, path: req.path, status: res.statusCode, durationMs: Date.now() - started }));
  next();
});
app.get('/health', (_req, res) => res.status(200).send('OK'));
app.get('/', (_req, res) => res.json({ app: 'embr-app-3-val', endpoints: ['GET /health', 'POST /messages', 'GET /messages/peek'], iteration: 2 }));
app.post('/messages', async (_req, res) => {
  if (!client) return res.status(503).json({ error: 'Service Bus namespace not configured' });
  const messageId = randomUUID();
  const sender = client.createSender(queue);
  log('servicebus.send.start', { queue, messageId });
  try {
    await sender.sendMessages({ messageId, body: { kind: 'builder-validation', sentAt: new Date().toISOString() } });
    log('servicebus.send.success', { queue, messageId });
    res.status(201).json({ messageId });
  } catch (error) {
    logError('servicebus.send.failure', error, { queue, messageId });
    res.status(502).json({ error: error.name, code: error.code });
  } finally { await sender.close().catch(error => logError('servicebus.sender.close.failure', error)); }
});
app.get('/messages/peek', async (_req, res) => {
  if (!client) return res.status(503).json({ error: 'Service Bus namespace not configured' });
  const receiver = client.createReceiver(queue);
  log('servicebus.peek.start', { queue });
  try {
    const messages = await receiver.peekMessages(10);
    const ids = messages.map(message => message.messageId);
    log('servicebus.peek.success', { queue, count: messages.length, messageIds: ids });
    res.json({ count: messages.length, messageIds: ids });
  } catch (error) {
    logError('servicebus.peek.failure', error, { queue });
    res.status(502).json({ error: error.name, code: error.code });
  } finally { await receiver.close().catch(error => logError('servicebus.receiver.close.failure', error)); }
});
const port = Number(process.env.PORT || 8080);
app.listen(port, '0.0.0.0', () => log('app.started', { port, namespaceConfigured: Boolean(namespace), queue, credential: 'ManagedIdentityCredential', identity: identityClientId ? 'user-assigned' : 'system-assigned', iteration: 2 }));
