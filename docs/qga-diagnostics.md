# Investigating an unresponsive Windows QEMU Guest Agent

This note records a read-only investigation of an agent timeout after Windows provisioning. It separates host observations from hypotheses; it is not a recovery procedure or a confirmed root-cause analysis. Private addresses, credentials, and operational logs are intentionally excluded.

## Confirmed observations

Two restored Windows guests initially accepted agent requests and later stopped answering `guest-ping`. Both remained running, with working TCP/RDP access. A control guest on the same node answered `guest-ping`.

Both affected QEMU processes have the configured agent socket, a `virtio-serial` device, and the `org.qemu.guest_agent.0` port. This confirms host-side configuration, not guest-side driver or service health. The failure followed captured asynchronous PowerShell execution; timing does not establish causation. Earlier assumptions that every disconnect indicated a reboot were not established by boot-time evidence.

Host versions inspected:

| Package | Version |
|---|---|
| pve-manager | 8.3.3 |
| pve-qemu-kvm | 9.0.2-5 |
| qemu-server | 8.3.6 |
| libpve-common-perl | 8.2.9 |

The project pins MSI package `110.2.3-2.el10` in `TemplateArtifactAcquirer.cs` and `TemplatePayloadStager.cs`. That package label must not be treated as the verified upstream version of the installed `qemu-ga.exe`.

## What the Proxmox error means

The installed `qemu-server 8.3.6` code was inspected directly, without modification:

- `/usr/share/perl5/PVE/QemuServer/Agent.pm`, `agent_available`, raises `QEMU guest agent is not running` when `qga_check_running` returns false.
- `/usr/share/perl5/PVE/QemuServer.pm`, `qga_check_running`, calls `guest-ping` with a three-second timeout. Any exception returns false; it does not inspect the Windows service state.
- Calling that same read-only check with warnings enabled exposed the underlying error on both guests: `qmp command 'guest-ping' failed - got timeout`.
- `Agent.pm`, `qemu_exec`, requests `capture-output: true` for native `qm guest exec` calls. Selecting asynchronous host execution does not disable output capture.

Inspected file SHA-256 values, for reproducibility:

```text
Agent.pm:      69068ee4e323409ed9fdcaee3da68650740b73411aaab318bf3fbc2ba3bf5979
QemuServer.pm: d24d774c092c32c825508835758da79b719134c0e68ec7d07abfaeddc3766aa1
```

These are installed-file observations, not claims that an unverified upstream branch matches the package byte-for-byte. Upstream project: [Proxmox qemu-server](https://git.proxmox.com/?p=qemu-server.git).

## What upstream source establishes

[QEMU v9.0.2 common agent commands](https://github.com/qemu/qemu/blob/v9.0.2/qga/commands.c) implements `guest-exec-status` by inspecting process completion. With captured output, it additionally waits for the stdout/stderr channels to close before reporting completion. This can explain a status remaining incomplete; it does **not**, by itself, explain why independent pings time out.

The same source implements asynchronous spawn and GLib output watches. [QEMU v9.0.2 Windows channel code](https://github.com/qemu/qemu/blob/v9.0.2/qga/channel-win32.c) uses Windows overlapped I/O. Its write path can wait for an outstanding write, and its dispatch path removes an errored source. These are possible inspection points if a running process has a stuck channel; they do not prove that either condition occurred here.

**Version limitation:** v9.0.2 is a pinned source reference, not a verified match for the installed Windows agent. The host QEMU version does not identify the guest agent binary version. Earlier research also read QEMU `master`; those observations are insufficient to identify a package-specific regression. No primary-source issue or fix matching this incident has been confirmed.

## Evidence needed to distinguish hypotheses

| Hypothesis | Discriminating guest evidence |
|---|---|
| Service stopped or crashed | Service state/PID/exit code; Service Control Manager and Application Error events |
| Service running but process or event loop stuck | Process lifetime/resource counters, then an approved process/thread diagnostic if needed |
| VirtIO serial device/channel fault | PnP status/error code, driver version, device/service events |
| Child output handles remain open | Process tree and handle evidence; incomplete execution status alone does not explain ping failure |

SMB and WinRM were unreachable during this investigation. RDP transport worked, but attempted UI automation did not produce a trustworthy guest collector result. No service-state, crash, binary-version, or driver-state result has therefore been established. Console keystrokes are not RDP-session input and must not be used as a substitute.

## Next decision

Collect service/process, installed binary version, serial device/driver, and relevant crash events through a confirmed interactive RDP session. Preserve that evidence before considering recovery. Do not run more `guest-exec` requests on an unresponsive channel to diagnose that channel.

If recovery is approved after evidence capture, restarting only the agent service may be preferable to rebooting the VM. A successful restart is recovery, not proof of root cause. Validate repeated host pings and a bounded minimal command before resuming heavier execution. Reproduction experiments require an isolated approved lab guest, not a production or control guest.

No guest reboot, service restart, reinstall, driver replacement, security relaxation, or source/image change was performed for this investigation. Shutdown and backup quiescing paths that depend on the agent cannot be assumed healthy while it is unresponsive.
