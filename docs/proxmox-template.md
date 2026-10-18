# Self-contained ISO and Proxmox VM template

English (default) | [Tiếng Việt](proxmox-template.vi.md)

## Scope

The opt-in profile builds an ISO containing the complete guest preparation payload. No post-install patch, Proxmox modification, host Apply script, or `cicustom` snippet is required.

- Validated with Tiny11 Core / Windows 11 Pro x64 build 26300.9457.
- Cloudbase-Init 1.1.9, QEMU Guest Agent 110.2.3, and generated ConfigDrive2.
- An embedded Cloudbase reconciliation plugin owns hostname, Administrator password, and IPv4 provisioning, including day-2 changes. Native ExtendVolumes expands C:.
- RDP is enabled after credential reconciliation, with NLA and Windows Firewall enabled. The approved inbound scope is Any.
- No host automation is configured to force guest time. Administrators can still adjust it manually; QGA RPCs, RTC settings, and Windows NTP are not restricted or changed for this purpose.

> Core removes Windows Update, Defender, and components that cannot be restored. Use only when these limitations are acceptable. Licensing and activation remain the operator's responsibility.

## Build and install

1. Build the Windows application as described in [usage](usage.md). Select Tiny11 Core and import a recipe containing `"TemplateProfile": "ProxmoxCloudbaseInit"`. The profile is off by default.
2. Use compatible Windows 11 Pro x64 media. The builder downloads and verifies pinned installers, then bundles agents, helpers, configuration, and manifests. It does not install guest agents on the build host.
3. Create an OVMF/UEFI VM with TPM 2.0, a 20 GiB SATA OS disk, and an E1000 NIC. Two CPUs and 4 GiB RAM were validated. Boot the ISO, select Pro, handle licensing/product-key prompts, and select the installation disk. No unattended disk wipe is configured.
4. After Setup, Audit preparation automatically installs agents, verifies payload and health, runs Sysprep generalize, and shuts down. Do not boot the prepared source again for testing.
5. Detach the installation ISO, retain OS-disk boot, add a native Cloud-Init drive using ConfigDrive2, and convert the stopped VM to a template.

The ISO is installation media, not a prepared VM template.

## Restore on another server

Validated archive: `vzdump-qemu-windows-11-tiny.vma.zst` (4,141,754,422 bytes).

```text
SHA-256: 04c325914c3c2a3e30e1828c1d7e6b61e428694b270d929a9c9e1efbde85cc7f
```

Copy the archive to the destination server and verify its checksum. Choose an unused VMID and storage with sufficient capacity:

```bash
sha256sum /path/template.vma.zst
qmrestore /path/template.vma.zst NEW_TEMPLATE_ID --storage TARGET_STORAGE --unique 1
qm config NEW_TEMPLATE_ID
```

Verify `template: 1`, the 20 GiB OS disk, EFI/TPM disks, and Cloud-Init drive. If restore does not preserve the template flag, keep the VM stopped and run `qm template NEW_TEMPLATE_ID`.

Map the network bridge through Hardware → Network Device. The validated configuration uses CPU `host` and Q35 machine version 9.0; choose a supported CPU/machine model for the destination environment after checking compatibility. Storage, bridge, and CPU mapping are standard configuration, not Proxmox patches.

Do not boot the template. Create a full clone:

```bash
qm clone NEW_TEMPLATE_ID NEW_VM_ID --full 1 --name YOUR_HOSTNAME
```

In Cloud-Init, set a unique Administrator password and the required IPv4, gateway, and DNS settings. Regenerate the Cloud-Init image before starting the clone. Proxmox generates ConfigDrive2; no custom snippets or shared passwords are needed. First boot can take several minutes, and hostname application may require an automatic reboot.

## Day-2 changes and disk growth

- Change hostname, password, or IP through the Proxmox GUI/API, regenerate the image, and perform a controlled reboot. Verify the resulting Windows configuration and authentication.
- Unchanged metadata does not overwrite direct Windows edits. Do not reset plugin state to force replay.
- Grow the OS disk through Hardware → Resize disk, then reboot for native ExtendVolumes to expand C:. Never shrink the disk. Keep the source template at 20 GiB.

## Verification and limitations

| Check | Result |
|---|---|
| Source/tests against v20 snapshot | 95 files matched SHA-256 before the English-only follow-up |
| Windows suite / publish / parser | 483/483 tests; publish and parser PASS in v20 receipts |
| Python reconciliation tests | 32/32 PASS |
| Clean ISO installation → Audit → Sysprep shutdown | PASS without guest patches |
| Two clones / SID-500 / credential isolation / day-2 A→B→A | PASS |
| Bounded CPU/RAM/RDP reliability | PASS across two runs, not one continuous 12-minute run |
| Stopped-source backup and native restore | PASS; template flag, EFI/TPM, and 20 GiB OS disk preserved |
| Clone from restored template | Password A→B→A, hostname/IP changes, 20→36 GiB growth, marker integrity, and RDP reconnect PASS |
| VSS | Writers/provider readiness PASS; live-backup consistency not tested |
| Gateway/DNS-only alternate endpoints | Deferred by the operator |
| Multiple physical servers | Not tested; native restore validated on the original node |

The v20 build runner did not capture its exit code. Independent output inspection passed, the prepared source was checked for consumed runtime state, and the node-side artifact hash was verified. Sysprep diagnostics retain BCD/WaaSMedic errors despite a success tag, shutdown, and successful clone tests; logs are not claimed to be error-free.

One earlier restored-clone boot displayed two Windows boot entries and required Enter; its cause remains unexplained. A newly recreated clone subsequently completed unattended first boot in 303 seconds with correct hostname, IP, and credentials. Controlled reboots and a cold boot also completed without key input. BCD was not patched; its default is the current Windows entry with a 30-second timeout.

Artifact pins and driver hashes are recorded in `C:\ProgramData\Tiny11Template\manifest.json`; `guest-files.json` records helper inventory. Each build's working-directory `report.json` records actual Core compatibility preservation. Pins are maintained in `TemplateArtifactAcquirer.cs` and `TemplatePayloadStager.cs`; do not replace report evidence with an assumed preservation list.

Before running a backup, inspect `/etc/vzdump.conf` and any existing hooks for side effects. The lab's global hook uploaded/moved the archive and ran retention; the archive was copied back and verified before restore. The template does not depend on that hook.

Validated ISO: `tiny11-core-proxmox-test-v20.iso`, 4,855,713,792 bytes, SHA-256 `A0BEEC347242D3D339A6CA2B1EA4EB884AB7950F8CD0A4EC779665C5289155F3`.
