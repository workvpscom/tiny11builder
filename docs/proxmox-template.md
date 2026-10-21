# Self-contained Proxmox Windows VM backup

English (default) | [Tiếng Việt](proxmox-template.vi.md)

## Scope

The opt-in `ProxmoxCloudbaseInit` profile builds installation media with pinned Cloudbase-Init, QEMU Guest Agent, serial/storage drivers, and automatic Audit preparation/Sysprep shutdown. Ordinary builds remain unchanged. No post-install patch, Proxmox modification, host Apply script, or `cicustom` is required.

The delivered archive is an **ordinary VM backup**, not a Proxmox template. Restore and start it directly; do not convert it to a template or clone it as part of this workflow.

> Tiny11 Core removes Windows Update, Defender, and components that cannot be restored. Licensing and activation remain the operator's responsibility.

## Build and prepare a clean VM

1. Follow [usage](usage.md), select fixed Tiny11 Core, and import a recipe containing `"TemplateProfile": "ProxmoxCloudbaseInit"`. Use compatible Windows 11 Pro x64 media.
2. Create an OVMF/UEFI VM with TPM2.0, `scsihw: virtio-scsi-single`, a **20 GiB `scsi0`** OS disk, and an E1000 NIC. Enable the native **QEMU Guest Agent** option before installation. Two CPUs and 4 GiB RAM were validated.
3. Boot the generated ISO. Handle licensing/product-key prompts and select the target disk. Setup includes the storage driver; no manual Load driver step or unattended disk wipe is required.
4. Let Audit preparation install and verify agents, then run Sysprep and shut down automatically. Do not boot the sealed source again.
5. Detach the installation ISO, set boot order to `scsi0`, add a native Cloud-Init drive, and select ConfigDrive2. Do not bake deployment passwords/IP settings into the source. **Do not run `qm template`.**
6. Back up the stopped ordinary VM with native `vzdump` and zstd. Inspect `/etc/vzdump.conf` and hooks first: existing hooks can upload/move archives or run retention. Do not copy lab hooks onto another server.

The storage package is pinned and injected offline into installation Windows and all boot images. See [driver provenance](virtio-scsi-driver.md).

## Restore and run directly

Archive: `vzdump-qemu-windows-11-tiny.vma.zst`, **3,836,770,028 bytes**.

```text
SHA-256: c80db4f5b2e63afef97d2b766974af3590e30fdbc34ff55e30f2b69fa0f009c5
```

```bash
sha256sum /path/vzdump-qemu-windows-11-tiny.vma.zst
qmrestore /path/vzdump-qemu-windows-11-tiny.vma.zst NEW_VM_ID --storage TARGET_STORAGE --unique 1
qm config NEW_VM_ID
```

Choose an unused VMID. Verify no enabled template flag, `scsi0`20GiB, `virtio-scsi-single`, EFI/TPM disks, native Cloud-Init drive, and boot order `scsi0`. Map the bridge and storage to the destination. The validated machine is Q35 9.0 with CPU `host`; the destination must support the selected machine/CPU configuration. These are native configuration choices, not node patches.

Before first Start, set a unique Administrator password, hostname, IPv4/gateway/DNS through Cloud-Init and regenerate its image. Start the restored VM directly. First boot may take several minutes and may reboot while applying hostname. No clone step is needed.

## Day-2 and disk growth

Change hostname, password, or static IPv4 through the GUI/API, regenerate ConfigDrive2, and perform a controlled reboot. Verify actual Windows configuration and authentication. The embedded Cloudbase reconciliation plugin owns these fields; unchanged metadata preserves direct Windows edits. Static-to-DHCP transitions are not implemented.

Grow the OS disk through Hardware → Resize disk, then reboot for native ExtendVolumes to expand C:. Never shrink the disk. Keep the clean source at20GiB.

RDP is enabled after credential reconciliation, with NLA and Windows Firewall enabled and the approved inbound scope Any. No host automation is configured to force guest time; administrator access remains unrestricted. This profile does not change RTC/NTP for that purpose.

## Verification and limitations

- Windows SDK8.0.426: **518/518 tests PASS, zero skipped**, including real DISM boot-image commit/reopen; win-x64 publish and Windows PowerShell5.1 parser PASS.
- ISO build: native runner/wrapper exit0, 32m17s. ISO size4,864,966,656 bytes; SHA-256 `DC0A27E2751E79D8B00A85A756C6B0706A301EF9E50D2EADEAE9A0706543D768`.
- Independent ISO inspection verified exact22-file payload, manifests, pins, absence of consumed runtime state, and boot-critical vioscsi in install.wim index1 and boot.wim indexes1/2.
- Clean installation recognized the20GiB SCSI disk without manual drivers, reached healthy boot-disk checks, prepared/Sysprepped/shut down automatically. Offline success tag and unconsumed state were verified; source disk hash was unchanged by backup.
- Native ordinary-VM restore/start completed unattended provisioning in approximately197s. Actual hostname/IP/routes/DNS, enabled SID-500, correct-versus-wrong credential authentication, healthy SCSI boot, and certificate-pinned RDP login/reconnect were verified. Remote archive SHA-256 matched local after upload.
- Historical v20 tests additionally covered password/hostname/static-IP A→B→A, disk growth with marker integrity, and bounded CPU/RAM/RDP load. Those broader scenarios were not all repeated on v21; do not treat historical SATA evidence as a new SCSI stress run.

No second-server field test or live-backup consistency proof was performed. This archive was backed up from a stopped source; VSS readiness is not live-backup certification. Gateway/DNS-only alternate endpoint tests remain deferred. Sysprep BCD/WaaSMedic diagnostics were retained despite success/shutdown; logs are not claimed to be error-free. Earlier buildhost QGA hangs were recovered, not proven root-cause fixed. Lab restore harness errors were retained separately from guest acceptance evidence.

Payload pins/licenses are recorded in `manifest.json` and `guest-files.json`; each build's `report.json` records actual compatibility preservation. No credentials or private lab captures are part of this patch.
