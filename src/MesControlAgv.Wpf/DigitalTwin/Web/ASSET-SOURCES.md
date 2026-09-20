# Offline viewer assets

Three.js 0.180.0 is distributed under MIT; see vendor/three/LICENSE.
The bundled Draco decoder is from the Three.js distribution, based on Google
Draco (Apache-2.0); see vendor/three/examples/jsm/libs/draco/README.md and its
source headers. The viewer does not load a CDN or contact instrument services.

lab606.glb is the user-provided laboratory model after approved optimization and
selective whole-device splitting. SHA-256:
96ca7f34c189c9c1add62c29441a7a9b16e67619c96659c259dca7ea8412671a

Source: res/606设备拆分-状态版/606已拆分场景.glb (2026-09-17 correction).
Selectable objects: combined AGV/arm, D160, SHA18i autosampler, opening/dispensing
workstation. The second chromatograph is now part of the static environment.
D160 and SHA18i are intentionally unbound pending communications; STN61_01 is
the ShineLab controller and is not an individual instrument's status identity.
Geometry detail and the 1,287,310 total triangles are unchanged.

Rebuild the vendored snapshot with scripts/digital-twin/prepare_wpf_assets.ps1.
These files are copied into build/publish output by the WPF project; the deployed
viewer does not require the repository, res directory, Node.js or a local server.
