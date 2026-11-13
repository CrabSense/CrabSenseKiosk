# Edge V2 controller worker

Configure trusted device identity and LAN IPv4 address once via deployment
configuration (environment variables or secret store, never commit keys):
 
EdgeGateway__EdgeV2Devices__0__DeviceCode
EdgeGateway__EdgeV2Devices__0__LocalIp (optional; omit for LAN registration discovery)
EdgeGateway__EdgeV2Devices__0__DeviceKey
EdgeGateway__EdgeV2Devices__0__LocalKey
 
Use HTTPS BackendUrl; an explicitly configured local IP must be a private IPv4 address. Otherwise the Kiosk learns the candidate from the registration socket, not the payload. It verifies the signed inventory before using the endpoint. The
controller must have the matching local HMAC key provisioned by physical USB.
No manual channel list or static IP required when registration reaches Kiosk directly. Signed manifest is read each cycle and uploaded to BE,
then commands are polled with X-Device-Key and X-Edge-Protocol: 2. Firmware must
support the signed inventory command and inventoryRevision. The worker checks
the lease, sequence, revision, identity and signed acknowledgement. It never
uses a telemetry-supplied URL. Separate per-device loops. Legacy worker no
longer sends unsigned commands; existing telemetry forwarding is unchanged.
 
Do not run the old reference Python bridge at the same time for these devices.
ON is still disabled in RC2 and BE deployment lock must remain enabled until
hardware tests. No physical load detection is claimed. Test on isolated LAN,
with loads disconnected, then validate real interlocks before any actuation.

The worker records the latest signed output snapshot through PUT /api/iot/controllers/{deviceCode}/ports/observations, including unbound channels, and updates existing RAS observations separately. A GPIO state is not proof that mains current flows or a pump operates.

The signed manifest is translated to the canonical POST /api/iot/controllers/capabilities DTO (configVersion, hardwareModel, portCode, type, GPIO/polarity/bus pins). No duplicate BE schema or legacy manifest proxy is required.
