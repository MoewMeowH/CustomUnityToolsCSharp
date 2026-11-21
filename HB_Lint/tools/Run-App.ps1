# 그래프 편집 앱을 실행한다: 웹 UI를 빌드해(npm, 처음엔 npm ci) src/HBLint.App/wwwroot/에 낸 뒤
# 기본은 창(hblint-app-desktop, WebView2 — 서버가 창과 같은 프로세스), -Browser면 서버(hblint-app)만 띄우고 기본 브라우저로 연다.
# 나머지 인자는 앱에 그대로 넘긴다. 예:
#   powershell -File tools\Run-App.ps1 --rules D:\Game\Assets\HBLint --out D:\Game\Assets\HBLint\Rules\LintRules.g.cs --project D:\Game
#   powershell -File tools\Run-App.ps1 -Browser --rules ...
# 필요한 것: .NET SDK 10 · Node.js (npm) · 창은 WebView2 런타임 (없으면 창이 브라우저로 열지 묻는다)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root 'src\HBLint.App\web'

$browser = $args -contains '-Browser'
$rest = @($args | Where-Object { $_ -ne '-Browser' })

if (-not (Test-Path (Join-Path $web 'node_modules'))) {
    npm ci --prefix $web --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm ci 실패 ($LASTEXITCODE)" }
}
npm run build --prefix $web
if ($LASTEXITCODE -ne 0) { throw "웹 UI 빌드 실패 ($LASTEXITCODE)" }

$project = if ($browser) { 'src\HBLint.App\HBLint.App.csproj' } else { 'src\HBLint.App.Desktop\HBLint.App.Desktop.csproj' }
dotnet run --project (Join-Path $root $project) -- @rest
