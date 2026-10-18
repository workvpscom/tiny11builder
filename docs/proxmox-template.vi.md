# ISO tự đủ và Proxmox VM template v20

[English (default)](proxmox-template.md) | Tiếng Việt

## Phạm vi bàn giao

Source hiện tại build ISO tích hợp payload; không cần patch Windows sau cài. Proxmox dùng chức năng chuẩn, không mod node, không host Apply script hoặc `cicustom`.

- Tiny11 Core / Windows 11 Pro x64, build26300.9457.
- Cloudbase-Init1.1.9, QGA110.2.3, ConfigDrive2.
- Embedded reconciliation plugin xử lý hostname/password/IPv4 first boot và day-2; native ExtendVolumes mở rộng C:.
- RDP bật sau credential reconciliation; NLA và Firewall giữ bật, remote scope Any theo approval.
- Không cấu hình host automation ép giờ; administrator vẫn chỉnh thủ công. Không block QGA RPC hoặc đổi RTC/NTP.

> Core loại bỏ Windows Update, Defender và nhiều component không thể phục hồi. Chỉ dùng khi chấp nhận đặc tính Core; không coi đây là Windows đầy đủ. License/activation do operator quản lý.

## Build và cài từ ISO

1. Build ứng dụng trên Windows theo [usage](usage.md), chọn Tiny11 Core, import recipe JSON có `"TemplateProfile": "ProxmoxCloudbaseInit"`. Profile mặc định off; không tự bật cho builds thường.
2. Dùng Windows11Pro x64 media phù hợp. Builder download/verify pinned installers và bundle helpers/config/manifest vào ISO; không cài agents trên build host.
3. Tạo VM UEFI/OVMF + TPM2.0, SATA OS disk20GiB, E1000 NIC; CPU2/RAM4GiB đã được nghiệm thu. Boot ISO, chọn Pro, xử lý license/product key hợp lệ và chọn disk cài. Không có unattended disk wipe.
4. Sau Setup, Audit preparation tự cài agents, verify payload/health, chạy Sysprep generalize và shutdown. Không boot lại VM nguồn để thử.
5. Tháo installation ISO; giữ boot OS disk. Thêm Cloud-Init drive chuẩn, chọn ConfigDrive2, convert thành template. Không cần chạy patch script.

Không coi ISO là template: ISO là media cài; template là VM đã chuẩn bị và shutdown.

## Restore archive trên server khác

Archive đã nghiệm thu: `vzdump-qemu-windows-11-tiny.vma.zst` (4.141.754.422 bytes).

```text
SHA-256: 04c325914c3c2a3e30e1828c1d7e6b61e428694b270d929a9c9e1efbde85cc7f
```

Copy archive sang storage backup server đích. Kiểm checksum trước restore. Chọn VMID trống và storage có đủ chỗ:

```bash
sha256sum /path/template.vma.zst
qmrestore /path/template.vma.zst NEW_TEMPLATE_ID --storage TARGET_STORAGE --unique 1
qm config NEW_TEMPLATE_ID
```

Kiểm `template: 1`, disk20GiB, EFI/TPM và Cloud-Init drive. Nếu phiên bản restore không giữ template flag, VM phải stopped rồi `qm template NEW_TEMPLATE_ID`.

Map bridge theo server đích qua GUI Hardware → Network Device. CPU đang `host`; có thể chọn CPU model phù hợp môi trường bằng GUI trước clone. Q35 machine version9.0 cần QEMU hỗ trợ; chỉ đổi compatibility model sau kiểm tương thích. Storage/bridge/CPU mapping là cấu hình native, không patch Proxmox.

Không boot template. Full clone sang VMID trống:

```bash
qm clone NEW_TEMPLATE_ID NEW_VM_ID --full 1 --name YOUR_HOSTNAME
```

Trong GUI Cloud-Init đặt Administrator password riêng, IPv4/gateway/DNS; regenerate image trước Start. Không dùng password mẫu hoặc credentials lab. ConfigDrive2 do Proxmox tự tạo; không cần custom snippets. Lần boot đầu có thể mất vài phút; hostname có thể cần reboot tự động.

## Day-2 và tăng disk

- Đổi hostname/password/IP qua GUI/API, regenerate Cloud-Init image, controlled reboot VM. Kiểm actual Windows config và đăng nhập mới.
- Không đổi metadata thì plugin không overwrite thay đổi trực tiếp trong Windows. Không reset plugin cache/state để ép chạy.
- Tăng virtual OS disk qua Hardware → Resize disk; reboot để native ExtendVolumes mở rộng C:. Không shrink. Nguồn/template giữ20GiB.

## Evidence và giới hạn

Follow-up Standards bổ sung deadline cho download/subprocess, diagnostics transform, skip rõ cho artifact tests và API docs. Harness SDK10 tạm đạt 7 tests, skip 5 optional artifact tests; lane bắt buộc thiếu prerequisites fail đúng cả 5 tests. Python đạt 32/32; read-only review không phát hiện thêm issue trong scope fix. Chưa chạy lại SDK8 chuẩn, Windows trust/process execution hoặc guest E2E. Preparation helper đã đổi: ISO/template v20 và receipts lịch sử không chứng nhận bytes source mới này.

| Hạng mục | Kết quả |
|---|---|
| Source/tests so với snapshot v20 | 95 file khớp SHA-256 trước follow-up chỉ dịch ngôn ngữ |
| Windows suite / publish / parser | 483/483, publish và parser PASS |
| Python reconciliation tests | 32/32 PASS |
| Clean ISO install → Audit → Sysprep shutdown | PASS; không patch guest |
| Hai clone / SID-500 / password isolation / day-2 A→B→A | PASS |
| Bounded CPU/RAM/RDP reliability | PASS qua hai run, không một run12phút |
| Native stopped-source backup → restore9104 | PASS, template/EFI/TPM/20GiB giữ |
| Clone từ restore | Password A→B→A, hostname/IP change, resize20→36GiB và RDP reconnect PASS |
| VSS | Writers/provider readiness PASS; chưa live-backup consistency test |
| Gateway/DNS-only alternate-endpoint | DEFERRED theo user |
| Nhiều server thực địa | Chưa có server thứ hai; native restore đã kiểm trên node hiện tại |

V20 build runner không capture được exit code; không báo exit0. Output độc lập đã inspect PASS, source state sạch và hash kiểm tại node. Có Sysprep BCD/WaaSMedic errors trong diagnostics dù success tag/shutdown và clone tests đạt; không gọi logs error-free.

Một restored-clone boot trước hiện menu hai Windows entries và cần Enter một lần; nguyên nhân observation đó chưa xác định. Đã recreate fresh clone từ restored template, đặt metadata trước Start và kiểm first boot không phím: Applied/login-ready sau303giây, đúng hostname/IP/password. Các controlled reboots và cold boot cũng tự hoàn tất. Không patch BCD; default current Windows, timeout30s.

Artifact pins và driver hashes nằm trong `C:\ProgramData\Tiny11Template\manifest.json`; helper inventory trong `guest-files.json` của payload/ISO. `report.json` trong working directory của mỗi build ghi Core compatibility preservation thực tế; source `TemplateArtifactAcquirer.cs` và `TemplatePayloadStager.cs` giữ pins đã kiểm. Không dùng danh sách preservation suy đoán thay report.

Trước backup, kiểm `/etc/vzdump.conf` và hook hiện có để tránh side effect ngoài dự kiến. Trong lab, global hook đã upload/move archive và chạy retention; archive được copy lại và verify trước restore. Template không phụ thuộc hook này.

ISO v20: `tiny11-core-proxmox-test-v20.iso`, 4.855.713.792 bytes, SHA-256 `A0BEEC347242D3D339A6CA2B1EA4EB884AB7950F8CD0A4EC779665C5289155F3`.
