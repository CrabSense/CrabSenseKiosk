# Camera recording software test

The worker is disabled by default. Use a dedicated local BE database and local media storage, never production. Install FFmpeg with libx264. Create a camera and a Continuous shared schedule in System Admin. Set matching, randomly generated 32+ character per-camera keys in BE and Kiosk through local environment or secret storage. Do not commit keys.

BE: CameraRecording__AgentKeys__<camera-guid>=<test-key>

Kiosk: CameraRecording__Enabled=true; CameraRecording__BackendUrl=http://127.0.0.1:5080; CameraRecording__Cameras__0__DeviceId=<camera-guid>; CameraRecording__Cameras__0__AgentKey=<test-key>; CameraRecording__Cameras__0__Synthetic=true; CameraRecording__SegmentSeconds=5; CameraRecording__MaxPendingClips=8.

Start BE and Kiosk, verify a completed execution and playable private MP4 in recording history. Interrupt media storage and verify journals remain in the per-camera spool; restore storage and verify retry, then pause schedule and verify no new clips. Check invalid key, stale revision, and a bad FFmpeg executable for failure reporting. The bounded capture/upload pipeline still restarts FFmpeg for each segment; it is not frame-gap-free continuous recording. Do not claim lossless 24/7 capture.
