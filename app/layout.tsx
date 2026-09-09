import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "狐声 FoxVoice — 本地实时变声器",
  description: "面向游戏和语音聊天的本地实时 RVC 变声工作台。",
  icons: {
    icon: "/favicon.svg",
    shortcut: "/favicon.svg",
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="zh-CN" className="dark">
      <body className="antialiased">{children}</body>
    </html>
  );
}
