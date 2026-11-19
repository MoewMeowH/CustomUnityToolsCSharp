# 분석기를 Release로 빌드해 Unity 패키지(unity/com.hb.lint/Analyzers/)로 복사한다.
# 분석기 코드를 바꿨으면 커밋 전에 돌린다. (.meta는 Unity가 만든 것을 그대로 둔다)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\HBLint.Analyzers\HBLint.Analyzers.csproj'
$built = Join-Path $root 'src\HBLint.Analyzers\bin\Release\netstandard2.0\HBLint.Analyzers.dll'
$target = Join-Path $root 'unity\com.hb.lint\Analyzers\HBLint.Analyzers.dll'

dotnet build $project -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "분석기 빌드 실패 ($LASTEXITCODE)" }

New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
Copy-Item $built $target -Force
$hash = (Get-FileHash $target -Algorithm SHA256).Hash
Write-Output "복사함: $target ($hash)"
