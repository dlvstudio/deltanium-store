Deltanium Store node
====================

This folder holds a ready-to-run Store node (no .NET install needed) and its installer. The installer asks
which network to join (mainnet or testnet), your one-shot voucher and how the network reaches you, then runs the
node as a service that starts with the computer.

How the network reaches you
  By default the node opens a DLTP (TCP) port, 7333, and tells Central the public IP it calls from
  ("tcps://auto:7333"): no domain, certificate or web server needed. Other nodes and the apps check your node
  with its key. Forward TCP port 7333 on your router to this computer. If your provider puts several homes
  behind one public IP (carrier-grade NAT), port forwarding cannot work: ask for a public IP, or use a small VPS.
  If you also run a website with HTTPS for the node, pass it with -HttpsEndpoint / --https-endpoint.

Windows (PowerShell, "Run as administrator", in this folder)
  .\install.ps1
  .\install.ps1 -Network testnet -Voucher YOUR-CODE -NonInteractive
  .\install.ps1 -Uninstall
  Service: DeltaniumStore. Program: C:\Program Files\Deltanium\Store. Data and node key: C:\ProgramData\Deltanium\Store.

Linux (systemd) and macOS (launchd)
  sudo ./install.sh
  sudo ./install.sh --network testnet --voucher YOUR-CODE --non-interactive
  sudo ./install.sh --uninstall
  Linux: service deltanium-store, program /opt/deltanium-store, data /var/lib/deltanium-store.
  macOS: daemon com.deltanium.store, program /usr/local/deltanium-store, data /Library/Application Support/Deltanium/Store.

Your node key
  On first start the node registers with your voucher and writes its key to node_info.json in the data folder,
  readable only by the service account and administrators. Back it up somewhere safe: it is your node's identity
  and where your earnings go. Never share it.

Check it works
  The node answers locally on http://127.0.0.1:5001/api/version, and appears on https://deltanium.com/nodes.html
  (add ?net=testnet for testnet). Logs are in the "logs" folder of the data folder.

Guide: https://deltanium.com/node-setup.html    Vouchers: https://github.com/dlvstudio/deltanium/blob/main/VOUCHERS.md
