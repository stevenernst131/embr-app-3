import { headers } from "next/headers";

export const dynamic = "force-dynamic";

export default async function Home() {
  const requestHeaders = await headers();
  const host = requestHeaders.get("host");
  const protocol = requestHeaders.get("x-forwarded-proto") ?? "http";
  const apiBaseUrl =
    process.env.API_BASE_URL ??
    (process.env.NODE_ENV === "development" || !host
      ? "http://localhost:8080"
      : `${protocol}://${host}/api`);
  let apiResult;

  try {
    const response = await fetch(`${apiBaseUrl}/message`, { cache: "no-store" });
    if (!response.ok) {
      throw new Error(`API returned ${response.status}`);
    }
    apiResult = await response.json();
  } catch (error) {
    apiResult = { message: `API unavailable: ${error.message}` };
  }

  return (
    <main>
      <p className="eyebrow">Embr builder sample</p>
      <h1>Next.js + separate API</h1>
      <p>The page is server-rendered by Next.js.</p>
      <p>
        Internal API response: <code>{apiResult.message}</code>
      </p>
    </main>
  );
}
