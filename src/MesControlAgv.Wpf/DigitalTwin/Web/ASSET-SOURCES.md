# Offline viewer assets

Three.js 0.180.0 is distributed under MIT; see vendor/three/LICENSE.
The bundled Draco decoder is from the Three.js distribution, based on Google
Draco (Apache-2.0); see vendor/three/examples/jsm/libs/draco/README.md and its
source headers. The viewer does not load a CDN or contact instrument services.

lab606.glb is the user-provided laboratory model after approved optimization and
selective whole-device splitting. SHA-256:
acb34c95e5c53c713d0a4c5246f90bb179c90ae9371626307621522c24df0e5a

Parent source: res/606设备拆分-状态版/606已拆分场景.glb (2026-09-17 correction),
SHA-256 96ca7f34c189c9c1add62c29441a7a9b16e67619c96659c259dca7ea8412671a.
On 2026-09-21 the user requested removal of the redundant white static robot
(188,464 faces) and the front box overlapping the workstation (288 faces).
The remaining 1,098,558 triangles retain their exact decoded positions/normals;
all node transforms and materials remain unchanged. The AGV/arm, D160 and SHA18i
compressed streams are copied byte-for-byte. See scripts/digital-twin/model-tools
and docs/DIGITAL-TWIN-MODEL-CLEANUP-2026-09-21.md for reproduction and evidence.
Selectable objects: combined AGV/arm, D160, SHA18i autosampler, opening/dispensing
workstation. The second chromatograph is now part of the static environment.
D160 and SHA18i are intentionally unbound pending communications; STN61_01 is
the ShineLab controller and is not an individual instrument's status identity.
There is only one robot in the visible geometry, the live combined AGV/arm.
The removed objects remain recoverable in Git history and the untouched parent
asset. Changed meshes use lossless sequential Draco to preserve even existing
degenerate faces; file size is 33,842,572 bytes (no extra geometry simplification).

Rebuild vendored dependencies with scripts/digital-twin/prepare_wpf_assets.ps1.
It defaults to this checked-in cleaned model; an explicit -ValidatedModelPath
must match the new SHA-256, so preparation cannot silently restore deleted items.
These files are copied into build/publish output by the WPF project; the deployed
viewer does not require the repository, res directory, Node.js or a local server.
