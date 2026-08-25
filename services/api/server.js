import { createServer } from "node:http";

const port = Number.parseInt(process.env.PORT ?? "8080", 10);

const server = createServer((request, response) => {
  const path = new URL(request.url, `http://${request.headers.host}`).pathname;

  if (path === "/health" || path === "/api/health") {
    return sendJson(response, 200, { status: "healthy" });
  }

  if (path === "/message" || path === "/api/message") {
    return sendJson(response, 200, {
      message: "Hello from the internal API component"
    });
  }

  sendJson(response, 404, { error: "Not found" });
});

server.listen(port, "0.0.0.0", () => {
  console.log(`Internal API listening on port ${port}`);
});

function sendJson(response, status, body) {
  response.writeHead(status, { "content-type": "application/json" });
  response.end(JSON.stringify(body));
}
