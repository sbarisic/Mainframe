param(
    [string]$VsInstall = "",
    [string]$Perl = "",
    [string]$VcToolsVersion = "14.44.35207"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$buildRoot = Join-Path $root 'artifacts/native'
$manifest = Get-Content (Join-Path $PSScriptRoot 'dependencies.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force $buildRoot | Out-Null
foreach ($name in @('sqlcipher', 'openssl', 'perl')) {
    $item = $manifest.$name
    $suffix = if ($name -eq 'perl') { 'zip' } else { 'tar.gz' }
    $archive = Join-Path $buildRoot "$name.$suffix"
    if (!(Test-Path $archive)) { Invoke-WebRequest $item.url -OutFile $archive }
    if ((Get-FileHash $archive).Hash -ne $item.sha256) { throw "Hash mismatch: $name" }
    $directory = if ($name -eq 'perl') { Join-Path $buildRoot 'perl' } else { Join-Path $buildRoot "$name-$($item.version)" }
    if (!(Test-Path $directory)) {
        if ($name -eq 'perl') { Expand-Archive $archive $directory }
        else { & tar -xf $archive -C $buildRoot; if ($LASTEXITCODE) { throw "Extraction failed: $name" } }
    }
}
if (!$VsInstall) {
    $VsInstall = & "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -version '[17.0,18.0)' -latest -property installationPath
}
if (!$VsInstall) { throw 'Visual Studio 2022 was not found. Supply -VsInstall and -VcToolsVersion for another installed C++ toolchain.' }
if ($VcToolsVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'VcToolsVersion must be a numeric MSVC version.' }
if (!$Perl) { $Perl = Join-Path $buildRoot 'perl/perl/bin/perl.exe' }
$vcvars = Join-Path $VsInstall 'VC/Auxiliary/Build/vcvars64.bat'
if (!(Test-Path $vcvars) -or !(Test-Path $Perl)) { throw 'Visual Studio 2022 C++ tools and Windows Perl are required.' }
$openssl = Join-Path $buildRoot "openssl-$($manifest.openssl.version)"
$sqlcipher = Join-Path $buildRoot "sqlcipher-$($manifest.sqlcipher.version)"
$runtime = Join-Path $buildRoot 'runtime'
New-Item -ItemType Directory -Force $runtime | Out-Null
Push-Location $openssl
try {
    & $Perl Configure VC-WIN64A no-asm no-shared no-tests "--prefix=$buildRoot/openssl-install"
    if ($LASTEXITCODE) { throw 'OpenSSL configure failed.' }
    & cmd /d /c "`"$vcvars`" -vcvars_ver=$VcToolsVersion && nmake /nologo build_libs > openssl-build.log 2>&1"
    if ($LASTEXITCODE) { throw 'OpenSSL build failed; see openssl-build.log.' }
} finally { Pop-Location }
Push-Location $sqlcipher
try {
    & cmd /d /c "`"$vcvars`" -vcvars_ver=$VcToolsVersion && nmake /nologo /f Makefile.msc sqlite3.c > amalgamation.log 2>&1"
    if ($LASTEXITCODE) { throw 'Amalgamation build failed.' }
    $arguments = '/nologo /O2 /MT /LD /DSQLITE_API=__declspec(dllexport) /DSQLITE_EXTRA_INIT=sqlcipher_extra_init /DSQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown /DSQLITE_HAS_CODEC /DSQLCIPHER_CRYPTO_OPENSSL /DSQLITE_TEMP_STORE=3 /DSQLITE_THREADSAFE=1 /DSQLITE_ENABLE_COLUMN_METADATA'
    & cmd /d /c "`"$vcvars`" -vcvars_ver=$VcToolsVersion && cl $arguments /I`"$openssl/include`" sqlite3.c `"$openssl/libcrypto.lib`" ws2_32.lib crypt32.lib advapi32.lib user32.lib /link /OUT:`"$runtime/sqlite3.dll`" > cipher-build.log 2>&1"
    if ($LASTEXITCODE) { throw 'SQLCipher compile failed; see cipher-build.log.' }
} finally { Pop-Location }
Copy-Item (Join-Path $PSScriptRoot '*-LICENSE.txt') $runtime
Get-FileHash (Join-Path $runtime 'sqlite3.dll') | Format-List
