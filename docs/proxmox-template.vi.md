# Backup VM Windows tự đủ cho Proxmox

[English (default)](proxmox-template.md) | Tiếng Việt

## Phạm vi

Profile opt-in `ProxmoxCloudbaseInit` build ISO chứa Cloudbase-Init, QGA, serial/storage drivers đã pin và tự động Audit preparation/Sysprep shutdown. Builds thường giữ nguyên behavior. Không patch sau cài, mod Proxmox, host Apply script hoặc `cicustom`.

Archive bàn giao là **backup VM thường**, không phải Proxmox template. Restore rồi Start trực tiếp; không convert template hoặc clone trong workflow này.

> Tiny11 Core loại bỏ Windows Update, Defender và các component không thể phục hồi. License/activation do operator quản lý.

## Giữ WebView2 Runtime (thay source; cần artifact mới)

Chỉ `TemplateProfile.ProxmoxCloudbaseInit` giữ WebView2 Runtime đã có sẵn qua cả bước `edge-files` và Core component pruning. Fixed Core removal set không đổi. Builds thường (`TemplateProfile.None`, kể cả recipe không khai profile) giữ behavior xóa trước đây.

Profile giữ `Microsoft\EdgeWebView`, shared binaries `Microsoft\EdgeCore` và Evergreen updater `Microsoft\EdgeUpdate` trong cả hai Program Files roots; giữ `Windows\System32\Microsoft-Edge-Webview` và WinSxS `microsoft-edge-webview` payloads (kể cả architecture/resource variants). Vẫn xóa cây browser `Microsoft\Edge` và Core payloads không liên quan. Giữ nguyên các cây shared thay vì đoán file/version nào xóa được: Microsoft mô tả [EdgeCore shared binaries](https://learn.microsoft.com/en-us/answers/questions/1060027/edge-enterprise-installation-without-folder-edgeco), [hard links và Runtime độc lập browser](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution), [update mechanism chung Edge/WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/enterprise). Giữ updater để không phá mechanism này, không khẳng định đã kiểm updates hoặc Runtime launch trên Core.

Đây là preservation, **không cài Runtime**. Không thêm installer hoặc download fallback. Thay đổi này chưa xác minh Runtime presence trong build source thực tế. Nếu source thiếu Runtime, preservation không cung cấp được; cần duyệt kế hoạch cài offline riêng trước khi khẳng định self-contained. Presence của files cũng không chứng minh WebView2 apps chạy được trên image đã strip.

ISO/archive hashes và evidence bên dưới thuộc artifact cũ, không phải nghiệm thu WebView2 change này. Không patch ISO/VM/backup hiện có. Sau review cần build **ISO mới**, clean install, kiểm Runtime detection và rendering của WebView2 app thật, rồi kiểm ordinary SCSI VM backup/restore. Windows SDK8 validation đạt 544 tests, zero failure/skip, gồm native WebView2 preservation và reboot-lifecycle regressions. Revision v23 đã build/inspect, clean install trên SCSI 20 GiB, tự reboot một lần vì Windows pending operations rồi tự seal bằng Sysprep. Ordinary stopped-VM backup đã restore sang VM riêng; ConfigDrive provisioning, kiểm password đúng/sai và RDP authenticated session đều PASS. Interactive WebView2 probe đạt Runtime initialization, HTML navigation, DOM/JavaScript, canvas pixel và PNG capture. Guest Runtime là `155.0.4283.62`; giữ updater nghĩa là version có thể khác offline image. Kết quả chỉ áp dụng artifact v23 đã kiểm, không bảo đảm mọi build tương lai.

### Xử lý reboot trong Audit

Clean SCSI installation của revision giữ Runtime phát hiện updater còn pending file-renames dù installers trả success. Finalizer từ chối Sysprep đúng guard, nhưng terminal failure làm Audit pass không resume được. VM lỗi chỉ giữ làm evidence, không bàn giao. Source mới kiểm actual Windows reboot flags ở cả preparation/finalization, dùng return code 2 của `WillReboot=OnRequest` để reboot và chạy lại cùng command. Journal thuộc manifest chỉ cho phép tối đa hai requested boots; pending dai dẳng vẫn fail thay vì reboot vô hạn. Không sửa Setup registry hoặc patch guest sau cài trong quy trình bàn giao. Source v23 đã đạt clean install, tự seal, ordinary backup/restore và WebView2 rendering thật. Restore harness đầu dùng hostname quá giới hạn Windows 15 ký tự và bị từ chối đúng guard; sửa Proxmox metadata rồi reboot qua đường hỗ trợ đã phục hồi provisioning, không patch guest/state.

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
