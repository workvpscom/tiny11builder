# Backup VM Windows tự đủ cho Proxmox

[English (default)](proxmox-template.md) | Tiếng Việt

## Phạm vi

Profile opt-in `ProxmoxCloudbaseInit` build ISO chứa Cloudbase-Init, QGA, serial/storage drivers đã pin và tự động Audit preparation/Sysprep shutdown. Builds thường giữ nguyên behavior. Không patch sau cài, mod Proxmox, host Apply script hoặc `cicustom`.

Archive bàn giao là **backup VM thường**, không phải Proxmox template. Restore rồi Start trực tiếp; không convert template hoặc clone trong workflow này.

> Tiny11 Core loại bỏ Windows Update, Defender và các component không thể phục hồi. License/activation do operator quản lý.

## Build và chuẩn bị VM sạch

1. Theo [usage](usage.md), chọn fixed Tiny11 Core và import recipe có `"TemplateProfile": "ProxmoxCloudbaseInit"`. Dùng Windows11Pro x64 media phù hợp.
2. Tạo VM OVMF/UEFI + TPM2.0, `scsihw: virtio-scsi-single`, OS disk **20GiB `scsi0`**, E1000 NIC. Bật native **QEMU Guest Agent** trước cài. Đã nghiệm thu2CPU/4GiB RAM.
3. Boot ISO mới, xử lý license/product-key prompts và chọn disk. Setup có storage driver sẵn, không Load driver thủ công hoặc unattended disk wipe.
4. Để Audit tự cài/kiểm agents, Sysprep và shutdown. Không boot nguồn sealed để thử.
5. Tháo ISO cài, đặt boot order `scsi0`, thêm Cloud-Init drive chuẩn với ConfigDrive2. Không bake password/IP deployment vào nguồn. **Không chạy `qm template`.**
6. Backup VM stopped bằng native `vzdump`/zstd. Kiểm `/etc/vzdump.conf` và hooks trước: hook có thể upload/move archive hoặc chạy retention. Không cài hook lab lên server khác.

Driver được pin và inject offline vào installation Windows và mọi boot image. Xem [provenance driver](virtio-scsi-driver.vi.md).

## Restore và chạy trực tiếp

Archive: `vzdump-qemu-windows-11-tiny.vma.zst`, **3.836.770.028 bytes**.

```text
SHA-256: c80db4f5b2e63afef97d2b766974af3590e30fdbc34ff55e30f2b69fa0f009c5
```

```bash
sha256sum /path/vzdump-qemu-windows-11-tiny.vma.zst
qmrestore /path/vzdump-qemu-windows-11-tiny.vma.zst NEW_VM_ID --storage TARGET_STORAGE --unique 1
qm config NEW_VM_ID
```

Chọn VMID trống. Kiểm không bật template flag, disk20GiB `scsi0`, `virtio-scsi-single`, EFI/TPM, Cloud-Init drive và boot order `scsi0`. Map storage/bridge theo server đích. Cấu hình đã nghiệm thu dùng Q35 9.0/CPU `host`; server đích cần hỗ trợ machine/CPU đã chọn. Đây là native configuration, không patch node.

Trước Start đầu tiên, đặt password Administrator riêng, hostname, IPv4/gateway/DNS qua Cloud-Init và regenerate image. Start bản restore trực tiếp. Boot đầu có thể mất vài phút và reboot để áp dụng hostname; không cần clone.

## Day-2 và tăng disk

Đổi hostname/password/static IPv4 qua GUI/API, regenerate ConfigDrive2 và controlled reboot. Kiểm actual Windows config/authentication. Embedded Cloudbase reconciliation plugin quản lý các fields này; metadata không đổi giữ direct Windows edits. Chưa hỗ trợ static→DHCP.

Tăng disk qua Hardware → Resize disk, rồi reboot để native ExtendVolumes mở rộng C:. Không shrink; giữ nguồn sạch20GiB.

RDP bật sau credential reconciliation, NLA/Firewall bật và inbound scope Any đã được duyệt. Không cấu hình host automation ép giờ; administrator vẫn có quyền chỉnh. Profile không đổi RTC/NTP cho mục đích này.

## Evidence và giới hạn

- Windows SDK8.0.426: **518/518 tests PASS, zero skip**, gồm real DISM commit/reopen boot images; publish win-x64 và PowerShell5.1 parser PASS.
- Build ISO: runner/wrapper exit0,32phút17giây. Size4.864.966.656 bytes; SHA-256 `DC0A27E2751E79D8B00A85A756C6B0706A301EF9E50D2EADEAE9A0706543D768`.
- Independent inspection kiểm payload22files, manifests/pins, không consumed runtime state; vioscsi boot-critical trong install.wim index1 và boot.wim indexes1/2.
- Clean install thấy diskSCSI20GiB không load driver thủ công, boot health đạt, tự preparation/Sysprep/shutdown. Offline success tag/state sạch được kiểm; disk hash nguồn không đổi sau backup.
- Native restore VM thường/Start hoàn tất provisioning không phím khoảng197giây. Hostname/IP/routes/DNS, SID-500 enabled, đúng/sai credential, SCSI boot Healthy và certificate-pinned RDP login/reconnect đều được kiểm. Remote archive SHA-256 khớp local sau upload.
- V20 lịch sử đã kiểm password/hostname/static-IP A→B→A, disk growth/marker và bounded CPU/RAM/RDP. Không lặp toàn bộ trên v21; không gọi SATA evidence là SCSI stress mới.

Chưa field-test server thứ hai hoặc live-backup consistency. Archive nguồn stopped; VSS readiness không chứng nhận live backup. Gateway/DNS-only alternate endpoints vẫn deferred. Sysprep BCD/WaaSMedic diagnostics giữ nguyên dù success/shutdown, không gọi logs error-free. Buildhost QGA hangs mới recovery, chưa chứng minh sửa root cause. Lỗi harness restore được giữ riêng với evidence guest acceptance.

Pins/licenses nằm trong `manifest.json` và `guest-files.json`; `report.json` ghi compatibility preservation thực tế. Patch không chứa credentials/private lab captures.
