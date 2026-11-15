# Device monitoring software test

Use an isolated test area and a provisioned test kiosk. Never use production actuator credentials for simulated devices.

1. Run Backend and the monitoring migration; configure Kiosk with EdgeGateway__BackendUrl and its provisioned EdgeGateway__KioskSecret (or kiosk-secret.txt). HTTPS is required for remote targets; HTTP loopback is allowed for local tests. Optional EdgeGateway__KioskLanIp is a LAN address.
2. Start Kiosk. Every 30 seconds the independent heartbeat worker posts to /api/kiosks/heartbeat using X-Kiosk-Key. The Backend records server time. Verify GET /api/system/device-monitoring/devices?type=kiosk with a System Administrator token. Kiosk Online does not prove that any controller or camera is Online.
3. Stop the Kiosk process. After 120 seconds the read API shows Offline. The existing alert sweep may take longer (5-minute cadence). Restart and verify recovery on heartbeat. Connection history samples approximately every 60 seconds, so short outages may be missed.
4. For controller health use the existing Edge V2 worker and configured device-specific credential plus trusted local signing key. Verify the signed manifest and output observations are accepted. Do not send ON commands or mark simulated ports physically commissioned.
5. For Camera, check agent heartbeat independently of camera heartbeat and recording execution. For Sensor, check latest measured/received times and configured timeout. Never infer live video, physical relay state, or genuine sensor readings from a simulated request.
6. Review history, observation gaps and trends in FE. A monitoring worker outage must not be interpreted as uptime.
