# Pinned VirtIO SCSI package

English (default) | [Tiếng Việt](virtio-scsi-driver.vi.md)

The opt-in Proxmox profile embeds the unmodified `vioscsi` Windows 11 AMD64 package from the official Fedora virtio-win archive. This is an additional storage package; the existing `vioser` files, hashes and `LICENSE.txt` remain unchanged.

## Provenance and trust

- Source: <https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/archive-virtio/virtio-win-0.1.240-1/virtio-win-0.1.240.iso>
- Whole ISO SHA-256: `EBD48258668F7F78E026ED276C28A9D19D83E020FFA080AD69910DC86BBCBCC6` (matched to the node ISO during package verification).
- Package directory: `vioscsi/w11/amd64`; INF version `100.93.104.24000`, dated `07/21/2023`.
- INF class: `SCSIAdapter`; service: `vioscsi`, `SERVICE_BOOT_START`.
- INF PCI IDs: `PCI\VEN_1AF4&DEV_1004` and `PCI\VEN_1AF4&DEV_1048`; specific entries include `SUBSYS_00081AF4&REV_00` and `SUBSYS_11001AF4&REV_01` respectively.
- Windows `Get-AuthenticodeSignature` verification of `vioscsi.cat`: `Status=Valid`, subject Microsoft Windows Hardware Compatibility Publisher, signer thumbprint `FAC666005546D6BE881A31C1267717879401A950`. Build-time verification requires a valid catalog signature and this pinned thumbprint for **both** driver catalogs.

| Embedded file | SHA-256 |
|---|---|
| `vioscsi.cat` | `F484461B7202A840D6F70ABC2EDFD82A01DD44349D234FAED9166D561D199B6A` |
| `vioscsi.inf` | `E49A966F7313DA4F6130DB00D84B57A167ADA0DE3D877C7CC584A7AED99C5BA2` |
| `vioscsi.sys` | `BE1FD2D12FA178D44CCC40548CC160D695A356566414E88976FC33F9F6C5AFA3` |
| `vioscsi-LICENSE.txt` | `ACCD84CB35EFF899D619FF40E6FEA8074305CDF0E7191DB71A5D9C3A015E3B02` |

`vioscsi-LICENSE.txt` preserves the ISO's `virtio-win_license.txt` exactly, including its 2009–2022 Red Hat copyright and BSD redistribution conditions. The existing vioser `LICENSE.txt` says 2009–2025; the notices are not byte-identical, so both are distributed. Scoped Git attributes prevent normalization of the signed INF and the original license. CAT/SYS bytes are copied unchanged.

The six CAT/INF/SYS hashes appear in the payload's `manifest.json`; `guest-files.json` also inventories both license notices. The profile stages `vioscsi` offline in install Windows and all boot images. It does not patch storage after installation. The profile remains off by default.

## Acceptance boundary

Package authenticity is not a clean-install test. Historical v20 ISO, SATA template, backup, restore and clone receipts do not certify SCSI support. Source review, canonical Windows validation and a newly built ISO must precede an isolated `scsi0` / `virtio-scsi-single` / 20 GiB clean install, Setup disk visibility, Audit preparation, Sysprep shutdown, cold boot and healthy pinned SCSI PnP observation. Existing published artifacts are unchanged.
