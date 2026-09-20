param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path)
$ErrorActionPreference = 'Stop'
$assetRoot = Join-Path $RepositoryRoot 'src/MesControlAgv.Wpf/DigitalTwin/Web'
$threeRoot = Join-Path $RepositoryRoot 'res/三维图纸/converted/.tools/node_modules/three'
$modelPath = Join-Path $RepositoryRoot 'res/606设备拆分-状态版/606已拆分场景.glb'
if (!(Test-Path -LiteralPath $threeRoot) -or !(Test-Path -LiteralPath $modelPath)) {
    throw '缺少已验证的拆分GLB或Three.js依赖，请按数字孪生交付记录准备资源。'
}
$package = Get-Content -Raw -Encoding UTF8 (Join-Path $threeRoot 'package.json') | ConvertFrom-Json
if ($package.version -ne '0.180.0') { throw 'Expected Three.js 0.180.0' }
$modelHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $modelPath).Hash.ToLowerInvariant()
if ($modelHash -ne '96ca7f34c189c9c1add62c29441a7a9b16e67619c96659c259dca7ea8412671a') {
    throw '拆分模型与已验证版本不一致，请重新核对后更新资源来源。'
}
$files = @('LICENSE','build/three.module.min.js','build/three.core.min.js',
    'examples/jsm/loaders/GLTFLoader.js','examples/jsm/loaders/DRACOLoader.js',
    'examples/jsm/controls/OrbitControls.js','examples/jsm/utils/BufferGeometryUtils.js',
    'examples/jsm/libs/draco/README.md','examples/jsm/libs/draco/gltf/draco_decoder.js',
    'examples/jsm/libs/draco/gltf/draco_wasm_wrapper.js','examples/jsm/libs/draco/gltf/draco_decoder.wasm')
foreach ($file in $files) {
    $destination = Join-Path $assetRoot ('vendor/three/' + $file)
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath (Join-Path $threeRoot $file) -Destination $destination
}
Copy-Item -LiteralPath $modelPath -Destination (Join-Path $assetRoot 'lab606.glb')
$dracoLicense = Join-Path $assetRoot 'vendor/three/examples/jsm/libs/draco/LICENSE'
if (!(Test-Path -LiteralPath $dracoLicense)) {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/google/draco/1.5.7/LICENSE' -OutFile $dracoLicense
}
Write-Output ('Prepared offline WebView2 assets: ' + $assetRoot)
