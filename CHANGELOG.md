# Changelog

## 0.1.1

- Fixed ROS 2 CDR 8-byte alignment after the encapsulation header.
- Verified live `sensor_msgs/msg/JointState` topic decoding over Foxglove CDR.
- Added support for flattened Foxglove action send-goal and get-result schemas.
- Verified live ROS 2 action execution with hidden action endpoints advertised.
- Added regression coverage for flattened action schemas and ROS 2 CDR alignment.

## 0.1.0

- Initial ROS#-compatible Foxglove WebSocket runtime client.
- Added topic publish/subscribe support.
- Added service client support.
- Added parameter get/set support.
- Added ROS 2 action client support for bridges that advertise standard action endpoints.
- Added explicit action endpoint discovery and helpful failures for default Foxglove bridges that hide action internals.
