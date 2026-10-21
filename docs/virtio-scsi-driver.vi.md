# Gói VirtIO SCSI được pin

[English (default)](virtio-scsi-driver.md) | Tiếng Việt

Profile Proxmox opt-in nhúng nguyên bản gói `vioscsi` Windows 11 AMD64 từ Fedora virtio-win archive chính thức. Đây là gói storage bổ sung; các file `vioser`, hashes và `LICENSE.txt` hiện có giữ nguyên.

## Provenance và trust

- Nguồn: [Fedora virtio-win 0.1.240-1](https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/archive-virtio/virtio-win-0.1.240-1/virtio-win-0.1.240.iso).
- SHA-256 toàn ISO: `EBD48258668F7F78E026ED276C28A9D19D83E020FFA080AD69910DC86BBCBCC6`, đã đối chiếu với ISO trên node.
- Thư mục: `vioscsi/w11/amd64`; INF version `100.93.104.24000`, ngày `07/21/2023`.
- INF class: `SCSIAdapter`; service: `vioscsi`, `SERVICE_BOOT_START`.
- PCI IDs: `PCI\VEN_1AF4&DEV_1004` và `PCI\VEN_1AF4&DEV_1048`; entries cụ thể lần lượt chứa `SUBSYS_00081AF4&REV_00` và `SUBSYS_11001AF4&REV_01`.
- Windows `Get-AuthenticodeSignature` cho catalog: `Status=Valid`, subject Microsoft Windows Hardware Compatibility Publisher, thumbprint `FAC666005546D6BE881A31C1267717879401A950`. Build yêu cầu signature hợp lệ và thumbprint này cho cả hai catalogs.

| File nhúng | SHA-256 |
|---|---|
| `vioscsi.cat` | `F484461B7202A840D6F70ABC2EDFD82A01DD44349D234FAED9166D561D199B6A` |
| `vioscsi.inf` | `E49A966F7313DA4F6130DB00D84B57A167ADA0DE3D877C7CC584A7AED99C5BA2` |
| `vioscsi.sys` | `BE1FD2D12FA178D44CCC40548CC160D695A356566414E88976FC33F9F6C5AFA3` |
| `vioscsi-LICENSE.txt` | `ACCD84CB35EFF899D619FF40E6FEA8074305CDF0E7191DB71A5D9C3A015E3B02` |

`vioscsi-LICENSE.txt` giữ nguyên `virtio-win_license.txt` trong ISO, gồm copyright Red Hat 2009–2022 và điều kiện phân phối BSD. License vioser hiện có ghi 2009–2025; hai notices khác bytes nên phân phối cả hai. Git attributes giữ nguyên INF và license; CAT/SYS không đổi.

Sáu hashes CAT/INF/SYS nằm trong payload `manifest.json`; `guest-files.json` kiểm kê cả hai license notices. Driver được stage offline vào installation image và mọi boot image, không patch storage sau cài. Profile mặc định tắt.

## Giới hạn nghiệm thu

Xác minh package không thay thế clean-install test. ISO/template SATA v20 và các receipts backup/restore/clone cũ không chứng nhận SCSI. Cần source review, Windows validation chuẩn và ISO mới trước khi kiểm clean install cô lập trên `scsi0` / `virtio-scsi-single` / 20 GiB: Setup thấy disk, Audit preparation, Sysprep shutdown, cold boot và SCSI PnP health đúng pins. Artifact đã publish giữ nguyên.
