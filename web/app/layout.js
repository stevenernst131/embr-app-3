import "./globals.css";

export const metadata = {
  title: "Next.js + separate API sample"
};

export default function RootLayout({ children }) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}

