# Driver control interfaces

> **Frozen (owner's decision of 2026-10-30).** SplitPlay ships no drivers of its own: no own IDD or gamepad driver, no
> Windows test mode, no EV certificate. Capture stays DXGI Desktop Duplication in the desk's RdpIdd session, gamepads
> stay on ViGEmBus (DualShock 4 by default). The sources in `drivers/idd`, `drivers/pad` and `native/rdsprov` stay in
> the repository and still build in CI (the drivers only when `drivers/**` changes), but the installer no longer ships
> them and nothing at run time opens these interfaces: the service, the seat and the worker do not load, install or
> register a SplitPlay driver or `splitplay-rdsprov.dll`. This page is kept as the record of the interface should the
> decision change; see [ARCHITECTURE.md §10](../ARCHITECTURE.md#10-решения-владельца-от-2026-10-30-без-своих-драйверов)
> for what is infeasible without them.

The two UMDF2 drivers are controlled only through the IOCTL headers below. Those headers are the contract;
this page says who calls what and in which order. Both headers are MIT and may be included by the GPL worker.

| Driver | Header | Hardware id | Device interface | Caller |
|---|---|---|---|---|
| SplitPlayIdd (IddCx remote-session IDD) | [`drivers/idd/include/splitplay/idd_ioctl.h`](../../drivers/idd/include/splitplay/idd_ioctl.h) | `SplitPlay_RemoteIdd` (returned by the protocol provider from `IWRdsWddmIddProps::GetHardwareId`) | `SP_GUID_DEVINTERFACE_IDD` {A6FC08D4-886A-4F4C-BE08-997FC7CB75E5} | SplitPlay.Service |
| SplitPlayPad (VHF gamepad parent) | [`drivers/pad/include/splitplay/pad_ioctl.h`](../../drivers/pad/include/splitplay/pad_ioctl.h) | `ROOT\SplitPlayPad` (root-enumerated, created by the installer) | `SP_GUID_DEVINTERFACE_PAD` {C4C04CE7-A979-4204-93B5-D49762025C52} | the desk worker |

All structures start with `size` and are little-endian and naturally aligned; fields are only ever appended.
`SP_IDD_IOCTL_VERSION` / `SP_PAD_IOCTL_VERSION` change only on incompatible changes.

## SplitPlayIdd

One adapter instance per remote session. The driver knows its Windows session id (from `...&SessionId_000N` in the
device instance id, or `DEVPKEY_Device_SessionId`), but not the desk id, which names the capture-ring objects.

Service sequence, after the desk's session is created and the IDD instance for it has arrived:

1. Enumerate interfaces of `SP_GUID_DEVINTERFACE_IDD` (`CM_Get_Device_Interface_ListW`) and open each one.
2. `SP_IOCTL_IDD_GET_INFO` → `SpIddInfo.session_id`; pick the instance whose session id is the desk's session.
3. `SP_IOCTL_IDD_ATTACH_DESK {session_id, desk_id}`. Until this call the driver acquires and counts frames but
   publishes nothing. Call it again after every worker restart (the call is idempotent).
4. Optional: `SP_IOCTL_IDD_SET_RENDER_ADAPTER` (desk `gpu` / profile `gpu_luid`) and `SP_IOCTL_IDD_SET_MODE`
   (the mode Moonlight asked for, relayed by the worker over IPC).
5. The worker opens the capture ring (`native/include/splitplay/capture_ring.h`) by desk id; it never opens the IDD.

The capture-ring section, event, textures and fence are created by the driver (WUDFHost, LocalService) and must carry
a DACL that grants the SYSTEM worker read access (section, textures) and synchronize/modify access (event, fence).

## SplitPlayPad

The worker opens the parent device and creates pads; pads belong to the handle, so a worker crash removes them.

1. Worker: `SP_IOCTL_PAD_CREATE` → `SpPadCreateOut.instance_id` of the new HID child.
2. Worker → service (IPC `event`, Stage 4): the instance id.
3. Service: locate the devnode and stamp `DEVPKEY_Device_SessionId = <desk session id>` on it, so the pad is visible
   only in that session; HidHide per-session rules are the backstop.
4. Worker: `SP_IOCTL_PAD_SUBMIT` per input report (hot path), `SP_IOCTL_PAD_GET_FEEDBACK` for rumble/lightbar,
   `SP_IOCTL_PAD_DESTROY` when the Moonlight controller goes away.

The driver never assigns pads to sessions itself; session isolation is the service's job.
