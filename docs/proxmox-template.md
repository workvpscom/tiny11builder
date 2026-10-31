# Self-contained Proxmox Windows VM backup

English (default) | [Tiếng Việt](proxmox-template.vi.md)

## Scope

The opt-in `ProxmoxCloudbaseInit` profile builds installation media with pinned Cloudbase-Init, QEMU Guest Agent, serial/storage drivers, and automatic Audit preparation/Sysprep shutdown. Ordinary builds remain unchanged. No post-install patch, Proxmox modification, host Apply script, or `cicustom` is required.

The delivered archive is an **ordinary VM backup**, not a Proxmox template. Restore and start it directly; do not convert it to a template or clone it as part of this workflow.

> Tiny11 Core removes Windows Update, Defender, and components that cannot be restored. Licensing and activation remain the operator's responsibility.

## WebView2 Runtime preservation (source change; new artifact required)

Only `TemplateProfile.ProxmoxCloudbaseInit` preserves existing WebView2 Runtime files during both `edge-files` removal and Core component pruning. The fixed Core removal set is unchanged. Ordinary builds (`TemplateProfile.None`, including omitted profile values) retain the previous removal behavior.

The profile keeps `Microsoft\EdgeWebView`, shared `Microsoft\EdgeCore` binaries, and the `Microsoft\EdgeUpdate` Evergreen updater under both Program Files roots, plus `Windows\System32\Microsoft-Edge-Webview` and WinSxS `microsoft-edge-webview` payloads (including architecture/resource variants). It still removes the `Microsoft\Edge` browser tree and unrelated Core payloads. Whole shared trees are retained rather than guessing which versioned files are safe to delete: [Microsoft describes shared EdgeCore binaries](https://learn.microsoft.com/en-us/answers/questions/1060027/edge-enterprise-installation-without-folder-edgeco), [shared Runtime/browser hard links and the independent Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution), and [the common Edge/WebView2 update mechanism](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/enterprise). Keeping the updater preserves that mechanism; it is not a claim that updates or Runtime launch have been validated on Core.

This is preservation, **not Runtime installation**. No installer or download fallback is added. Runtime presence in the actual build source has not been verified by this change. If it is missing, preservation cannot supply it; an approved offline installation plan is needed before claiming a self-contained result. File presence alone also does not prove that WebView2 apps work on the stripped image.

The ISO/archive hashes and acceptance evidence below describe the earlier artifact, not this WebView2 change. Existing ISO/VM/backup files have not been patched. After review, build a **new ISO**, perform a clean installation, verify Runtime detection and a real WebView2 application's rendering, then validate the ordinary SCSI VM backup/restore workflow. Windows SDK8 validation passed 544 tests with zero failures or skips, including native WebView2 preservation cases and reboot-lifecycle regressions. Revision v23 was built and inspected, clean-installed on a 20 GiB SCSI disk, automatically rebooted once for pending Windows operations, and automatically sealed with Sysprep. An ordinary stopped-VM backup was restored to a separate VM; ConfigDrive provisioning, positive/negative password checks and an authenticated RDP session passed. An interactive WebView2 probe passed Runtime initialization, HTML navigation, DOM/JavaScript, canvas-pixel and PNG-capture checks. The guest Runtime was `155.0.4283.62`; updater preservation means its version can differ from the offline image. These results apply to the tested v23 artifact, not every future build.

### Audit reboot handling

A clean SCSI installation of the preservation revision exposed updater file-renames pending after installers returned success. Finalization correctly refused Sysprep, but its terminal failure made that Audit pass non-resumable. That failed VM is diagnostic evidence, not a deliverable. The source now checks actual Windows reboot flags in preparation and finalization, and requests the supported `WillReboot=OnRequest` return code 2 to rerun the same command after reboot. A manifest-owned journal allows at most two requested boots; persistent reboot requirements remain a failure rather than an endless loop. No Setup registry repair or post-install guest patch is part of delivery. The corrected v23 source passed clean-install, automatic seal, ordinary backup/restore and real WebView2 rendering validation. The first restore harness used a hostname longer than Windows' 15-character limit and was correctly rejected; correcting Proxmox metadata and using the supported reboot path recovered provisioning without guest/state patches.

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
