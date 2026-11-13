# Edge V2 canonical controller discovery

The Kiosk consumes the signed heartbeat `capabilities` returned by hardware-device-controller and forwards it directly to POST /api/iot/controllers/capabilities. The cloud's accepted configVersion, boundOutputChannels, and hardwareControlDisabled response is checked before leasing commands. If sequence storage is not ready or control is globally locked, only inventory and observations are sent. OFF can address a current reported output; ON requires a cloud-verified binding and remains physically locked in the current firmware.

Configure trusted device identity once using a secret store:
- EdgeGateway__EdgeV2Devices__0__DeviceCode
- EdgeGateway__EdgeV2Devices__0__DeviceKey
- EdgeGateway__EdgeV2Devices__0__LocalKey
- EdgeGateway__EdgeV2Devices__0__LocalIp (optional; otherwise learned from registration socket and verified with HMAC)

BackendUrl must use HTTPS. No manual channel list. Never commit keys. Command envelopes are revision-bound and signed; ACK uses the current delivery attempt and verified response. PUT /api/iot/controllers/{deviceCode}/ports/observations stores current signed GPIO observations; legacy RAS status is updated only for verified bindings. GPIO state is not proof of load operation. Do not run the Python reference bridge simultaneously. No FE changes. Requires BE revision-bound command and observation update and compatible hardware firmware.
