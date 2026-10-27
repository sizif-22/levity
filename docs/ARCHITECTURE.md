# Architecture

<!-- levity:diagram:start -->
## Architecture

Levity is a Windows tray assistant and developer sidebar for the Antigravity CLI \(agy\), providing repo switching, PowerShell task execution, automated architecture diagram generation, and remote access from a mobile web app\. The WPF desktop app coordinates local agy child processes and background jobs while bridging messages to an embedded WebView2 UI and an end\-to\-end encrypted Cloudflare Worker WebSocket relay\. Remote paired phones interact with the desktop session over this encrypted relay without requiring incoming open ports on the host PC\.

```mermaid
flowchart TD

  n1["Main Window Controller"]
  n2["Sidebar WebView2 UI"]
  n3["Tray Icon Manager"]
  n4["Agy Process Manager"]
  n5[("App Settings & Storage")]
  n6["Project Scanner"]
  n7["Project Sync Service"]
  n8["Mobile Hub Coordinator"]
  n9["Relay Client"]
  n10["Relay Crypto (E2EE)"]
  n11["Relay Net & DoH"]
  n12{{"Antigravity CLI (agy)"}}
  n13["Diagram Service"]
  n14["Diagram Ingest & Sanitize"]
  n15["Diagram Validator"]
  n16["Diagram Compiler (Mermaid)"]
  n17["Agy Diagram Runner"]
  n18["Relay Durable Object"]
  n19["Mobile App Entry & UI"]
  n20["Mobile Sheet Components"]
  n21["Mobile Chat Protocol"]
  n22["Mobile Pairing Manager"]
  n23["Mobile WebCrypto (E2EE)"]
  n24[("IndexedDB Device Store")]
  n25["Mobile Relay Connection"]

  n3 -->|"toggles window"| n1
  n1 -.->|"hosts webview"| n2
  n1 -->|"manages process"| n4
  n1 -->|"reads settings"| n5
  n1 -->|"routes remote msgs"| n8
  n1 -->|"runs batch reviews"| n7
  n1 -->|"executes diagrams"| n13
  n4 -.->|"spawns process"| n12
  n13 -->|"ingests files"| n14
  n13 -.->|"spawns model runner"| n17
  n13 -->|"validates graph"| n15
  n13 -->|"compiles mermaid"| n16
  n13 -->|"reads settings"| n5
  n7 -->|"scans repos"| n6
  n7 -->|"executes agent"| n4
  n8 -->|"connects websocket"| n9
  n8 -->|"encrypts frames"| n10
  n9 -->|"resolves and connects"| n11
  n11 -.->|"wss connection"| n18
  n19 -->|"renders sheets"| n20
  n19 -->|"maintains state"| n21
  n19 -->|"wss to relay"| n25
  n22 -->|"encrypts & rooms"| n23
  n22 -->|"persists credentials"| n24
  n25 -.->|"websocket connection"| n18

  classDef tone0 fill:#dbeafe,stroke:#2563eb,stroke-width:1.5px,color:#172554
  classDef tone1 fill:#fef3c7,stroke:#d97706,stroke-width:1.5px,color:#78350f
  classDef tone2 fill:#dcfce7,stroke:#16a34a,stroke-width:1.5px,color:#14532d
  classDef tone3 fill:#ffe4e6,stroke:#e11d48,stroke-width:1.5px,color:#881337
  classDef tone4 fill:#e0e7ff,stroke:#4f46e5,stroke-width:1.5px,color:#312e81
  classDef tone5 fill:#ccfbf1,stroke:#0f766e,stroke-width:1.5px,color:#134e4a
  classDef toneNeutral fill:#f8fafc,stroke:#334155,stroke-width:1.5px,color:#0f172a
  class n12 toneNeutral
  class n1,n2,n3 tone0
  class n4,n5,n6,n7,n8,n9,n10,n11 tone1
  class n13,n14,n15,n16,n17 tone2
  class n18 tone3
  class n19,n20,n21,n22,n23,n24,n25 tone4
```

Colours: Desktop Application \(WPF\); Desktop Core Services; Diagram Generation Engine; Cloudflare Relay; Mobile Web App \(React\) (in this order: blue, amber, green, rose, indigo, teal, then repeating).

Solid arrows are backed by a reference in the code. Dashed arrows are inferred: plausible, but no reference was found in the files that were read.

### Components

- **Main Window Controller**: [`MainWindow.xaml.cs`](../MainWindow.xaml.cs)
- **Sidebar WebView2 UI**: [`wwwroot/app.js`](../wwwroot/app.js)
- **Tray Icon Manager**: [`Services/TrayIconManager.cs`](../Services/TrayIconManager.cs)
- **Agy Process Manager**: [`Services/AgyProcessManager.cs`](../Services/AgyProcessManager.cs)
- **App Settings &amp; Storage**: [`Services/AppSettings.cs`](../Services/AppSettings.cs)
- **Project Scanner**: [`Services/ProjectScanner.cs`](../Services/ProjectScanner.cs)
- **Project Sync Service**: [`Services/ProjectSyncService.cs`](../Services/ProjectSyncService.cs)
- **Mobile Hub Coordinator**: [`Services/MobileHub.cs`](../Services/MobileHub.cs)
- **Relay Client**: [`Services/RelayClient.cs`](../Services/RelayClient.cs)
- **Relay Crypto \(E2EE\)**: [`Services/RelayCrypto.cs`](../Services/RelayCrypto.cs)
- **Relay Net &amp; DoH**: [`Services/RelayNet.cs`](../Services/RelayNet.cs)
- **Antigravity CLI \(agy\)**
- **Diagram Service**: [`Services/DiagramService.cs`](../Services/DiagramService.cs)
- **Diagram Ingest &amp; Sanitize**: [`Services/DiagramIngest.cs`](../Services/DiagramIngest.cs)
- **Diagram Validator**: [`Services/DiagramValidator.cs`](../Services/DiagramValidator.cs)
- **Diagram Compiler \(Mermaid\)**: [`Services/DiagramCompiler.cs`](../Services/DiagramCompiler.cs)
- **Agy Diagram Runner**: [`Services/DiagramService.cs`](../Services/DiagramService.cs)
- **Relay Durable Object**: [`relay/src/index.ts`](../relay/src/index.ts)
- **Mobile App Entry &amp; UI**: [`web/src/App.tsx`](../web/src/App.tsx)
- **Mobile Sheet Components**: [`web/src/components/ProjectsSheet.tsx`](../web/src/components/ProjectsSheet.tsx)
- **Mobile Chat Protocol**: [`web/src/lib/chat.ts`](../web/src/lib/chat.ts)
- **Mobile Pairing Manager**: [`web/src/lib/pairing.ts`](../web/src/lib/pairing.ts)
- **Mobile WebCrypto \(E2EE\)**: [`web/src/lib/crypto.ts`](../web/src/lib/crypto.ts)
- **IndexedDB Device Store**: [`web/src/lib/store.ts`](../web/src/lib/store.ts)
- **Mobile Relay Connection**: [`web/src/lib/relay.ts`](../web/src/lib/relay.ts)

_Generated by Levity on 2026-10-27 from commit `a9fd3c8`. Re-run it to refresh this section; text outside the markers is never touched._
<!-- levity:diagram:end -->
