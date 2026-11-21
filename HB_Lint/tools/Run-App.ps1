# 그래프 편집 앱 hblint-app을 실행한다: 웹 UI를 빌드해(npm, 처음엔 npm ci) src/HBLint.App/wwwroot/에 낸 뒤 서버를 띄운다.
# 인자는 hblint-app에 그대로 넘긴다. 예:
#   powershell -File tools\Run-App.ps1 --rules D:\Game\Assets\HBLint --out D:\Game\Assets\HBLint\Rules\LintRules.g.cs --project D:\Game
# 필요한 것: .NET SDK 10 · Node.js (npm)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root 'src\HBLint.App\web'

if (-not (Test-Path (Join-Path $web 'node_modules'))) {
    npm ci --prefix $web --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm ci 실패 ($LASTEXITCODE)" }
}
npm run build --prefix $web
if ($LASTEXITCODE -ne 0) { throw "웹 UI 빌드 실패 ($LASTEXITCODE)" }

dotnet run --project (Join-Path $root 'src\HBLint.App\HBLint.App.csproj') -- @args
