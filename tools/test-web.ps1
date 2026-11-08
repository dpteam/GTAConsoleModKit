<#
    HTTP API + static tests for DPT.PSP GTA Modding Kit.

    The server must already be running (tools\ci-test.ps1 starts it).
    Usage:  powershell -File tools\test-web.ps1 [-Port 44700]
#>
param(
    [int]$Port = 44700,
    [string]$Base = ""
)

$ErrorActionPreference = 'Stop'
if (-not $Base) { $Base = "http://127.0.0.1:$Port" }

$script:passed = 0
$script:failed = 0

function Check([string]$label, [scriptblock]$test) {
    try {
        $result = & $test
        if ($result) {
            $script:passed++
            Write-Host ("  PASS  " + $label) -ForegroundColor Green
        } else {
            $script:failed++
            Write-Host ("  FAIL  " + $label) -ForegroundColor Red
        }
    } catch {
        $script:failed++
        Write-Host ("  FAIL  " + $label + " :: " + $_.Exception.Message) -ForegroundColor Red
    }
}

function Get-Json([string]$path) {
    return Invoke-RestMethod -Uri ($Base + $path) -UseBasicParsing
}

function Post-Json([string]$path, $body) {
    $json = $body | ConvertTo-Json -Depth 6
    return Invoke-RestMethod -Uri ($Base + $path) -Method Post -ContentType 'application/json' -Body $json -UseBasicParsing
}

Write-Host ""
Write-Host "HTTP tests against $Base" -ForegroundColor Cyan

# ------------------------------------------------------------------ basics
Check "health answers ok" {
    $r = Get-Json "/api/health"
    $r.ok -eq $true -and $r.port -eq $Port
}

Check "status has counters and workspace" {
    $r = Get-Json "/api/status"
    $r.ok -eq $true -and $r.projects -ge 0 -and $r.workspace.Length -gt 0
}

Check "index.html served as html" {
    $r = Invoke-WebRequest -Uri ($Base + "/") -UseBasicParsing
    $r.StatusCode -eq 200 -and $r.Headers['Content-Type'] -like '*text/html*' -and $r.Content -like '*DPT.PSP*'
}

Check "css and js served" {
    $css = Invoke-WebRequest -Uri ($Base + "/assets/css/app.css") -UseBasicParsing
    $js = Invoke-WebRequest -Uri ($Base + "/assets/js/app.js") -UseBasicParsing
    $css.StatusCode -eq 200 -and $js.StatusCode -eq 200 -and $js.Content -like '*DPT.PSP*'
}

Check "js is utf-8 with readable russian text" {
    $js = Invoke-WebRequest -Uri ($Base + "/assets/js/app.js") -UseBasicParsing
    $text = [System.Text.Encoding]::UTF8.GetString($js.RawContentStream.ToArray())
    $text.Contains('Распаковать ISO')
}

Check "unknown api endpoint 404" {
    try { Get-Json "/api/nope" | Out-Null; $false } catch { $_.Exception.Response.StatusCode.value__ -eq 404 }
}

Check "path traversal denied" {
    try { Get-Json "/api/projects/x/inspect?path=../../../etc/passwd" | Out-Null; $false }
    catch { $_.Exception.Response.StatusCode.value__ -in @(403, 404) }
}

# ---------------------------------------------------------------- projects
$name = "ci test " + (Get-Date -Format 'HHmmss')
$projectId = $null

Check "create project" {
    $r = Post-Json "/api/projects" @{ name = $name; game = 'LCS'; owner = 'ci'; description = 'auto test' }
    $script:projectId = $r.id
    $r.ok -eq $true -and $r.code -match '^[A-Z2-9]{4}-[A-Z2-9]{4}$'
}

Check "project appears in the list" {
    $r = Get-Json "/api/projects"
    (@($r.projects | Where-Object { $_.id -eq $script:projectId })).Count -eq 1
}

Check "project manifest has a session code and ops" {
    $r = Get-Json ("/api/projects/" + $script:projectId)
    $r.ok -eq $true -and $r.project.code.Length -eq 9 -and $r.ops.Count -ge 1
}

# upload a generated CHK-like file: we build a tiny valid file by hand is overkill,
# instead upload text and check the pipeline stores and identifies it.
Check "upload stores the file and logs an op" {
    $body = [System.Text.Encoding]::UTF8.GetBytes("hello modkit")
    $r = Invoke-RestMethod -Uri ($Base + "/api/projects/" + $script:projectId + "/upload?path=docs/hello.txt") `
        -Method Post -ContentType 'application/octet-stream' -Body $body -UseBasicParsing
    $file = Get-Json ("/api/projects/" + $script:projectId + "/files")
    $r.ok -eq $true -and (@($file.files | Where-Object { $_.path -eq 'docs/hello.txt' })).size -eq 12
}

Check "inspect identifies a text file" {
    $r = Get-Json ("/api/projects/" + $script:projectId + "/inspect?path=docs/hello.txt")
    $r.ok -eq $true -and $r.kind.Length -gt 0
}

Check "file download returns exact bytes" {
    $r = Invoke-WebRequest -Uri ($Base + "/api/projects/" + $script:projectId + "/file?path=docs/hello.txt") -UseBasicParsing
    [System.Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray()) -eq "hello modkit"
}

# ------------------------------------------------------------ working tree

Check "add folder copies a tree recursively" {
    # the source must live inside an allowed root, so build it under the workspace
    $ws = (Get-Json "/api/status").workspace
    $source = Join-Path $ws ("ci-src-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path (Join-Path $source 'a\b') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $source 'e') -Force | Out-Null
    Set-Content -Path (Join-Path $source 'a\b\deep.txt') -Value 'deep' -NoNewline
    Set-Content -Path (Join-Path $source 'top.txt') -Value 'top' -NoNewline
    Set-Content -Path (Join-Path $source 'e\side.txt') -Value 'side' -NoNewline

    $r = Post-Json ("/api/projects/" + $script:projectId + "/folder") @{ path = $source; as = 'added' }
    $files = Get-Json ("/api/projects/" + $script:projectId + "/files")
    $paths = @($files.files | ForEach-Object { $_.path })
    Remove-Item -Recurse -Force $source -ErrorAction SilentlyContinue

    $r.ok -eq $true -and $r.files -eq 3 -and
        ($paths -contains 'added/a/b/deep.txt') -and
        ($paths -contains 'added/top.txt') -and
        ($paths -contains 'added/e/side.txt')
}

Check "mkdir creates a folder and delete removes it" {
    $r = Post-Json ("/api/projects/" + $script:projectId + "/mkdir") @{ path = 'added/empty' }
    $del = Post-Json ("/api/projects/" + $script:projectId + "/delete") @{ path = 'added/empty' }
    $r.ok -eq $true -and $r.path -eq 'added/empty' -and $del.ok -eq $true -and $del.folder -eq $true
}

Check "delete removes a file and then the whole folder" {
    $file = Post-Json ("/api/projects/" + $script:projectId + "/delete") @{ path = 'added/top.txt' }
    $afterFile = Get-Json ("/api/projects/" + $script:projectId + "/files")
    $gone = -not (@($afterFile.files | ForEach-Object { $_.path }) -contains 'added/top.txt')

    $folder = Post-Json ("/api/projects/" + $script:projectId + "/delete") @{ path = 'added' }
    $afterFolder = Get-Json ("/api/projects/" + $script:projectId + "/files")
    $empty = @($afterFolder.files | Where-Object { $_.path -like 'added/*' }).Count -eq 0

    $file.ok -eq $true -and $gone -and $folder.ok -eq $true -and $folder.folder -eq $true -and $empty
}

Check "delete and add folder guard the project root and traversal" {
    $root = $false
    $escape = $false
    try { Post-Json ("/api/projects/" + $script:projectId + "/delete") @{ path = '' } | Out-Null }
    catch { $root = $_.Exception.Response.StatusCode.value__ -in @(400, 403) }
    try { Post-Json ("/api/projects/" + $script:projectId + "/delete") @{ path = '../../etc' } | Out-Null }
    catch { $escape = $_.Exception.Response.StatusCode.value__ -in @(400, 403) }
    $root -and $escape
}

Check "fs browser lists roots and folders, refuses outside paths" {
    $roots = Get-Json "/api/fs"
    $inside = Get-Json ("/api/fs?path=" + [uri]::EscapeDataString($roots.roots[0].path))
    $denied = $false
    try { Get-Json ("/api/fs?path=" + [uri]::EscapeDataString('C:\Windows')) | Out-Null }
    catch { $denied = $_.Exception.Response.StatusCode.value__ -eq 403 }
    $roots.ok -eq $true -and $roots.roots.Count -ge 1 -and $inside.ok -eq $true -and $denied
}

Check "a copied read-only file becomes writable" {
    $ws = (Get-Json "/api/status").workspace
    $source = Join-Path $ws ("ci-ro-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    $file = Join-Path $source 'packed.idx'
    Set-Content -Path $file -Value 'read only' -NoNewline
    (Get-Item $file).Attributes = 'ReadOnly'

    $r = Post-Json ("/api/projects/" + $script:projectId + "/folder") @{ path = $source; as = 'ro' }
    $copied = Join-Path (Join-Path $ws ($script:projectId + '\files')) 'ro\packed.idx'
    $writable = (Test-Path $copied) -and -not ((Get-Item $copied).Attributes -band [IO.FileAttributes]::ReadOnly)

    # and the folder with a read-only file must delete completely
    $del = Post-Json ("/api/projects/" + $script:projectId + "/delete") @{ path = 'ro' }
    Remove-Item -Recurse -Force $source -ErrorAction SilentlyContinue
    $r.ok -eq $true -and $writable -and $del.ok -eq $true
}

# ---------------------------------------------------------------- sessions
$clientId = $null

Check "session join returns a client id" {
    $r = Post-Json ("/api/projects/" + $script:projectId + "/session/join") @{ nick = 'tester'; clientId = '' }
    $script:clientId = $r.clientId
    $r.ok -eq $true -and $r.clientId.Length -ge 8
}

Check "session lists the participant" {
    $r = Get-Json ("/api/projects/" + $script:projectId + "/session")
    (@($r.members | Where-Object { $_.nick -eq 'tester' })).Count -eq 1
}

Check "note lands in the op log" {
    Post-Json ("/api/projects/" + $script:projectId + "/note") @{ text = 'ci note'; user = 'tester' } | Out-Null
    $r = Get-Json ("/api/projects/" + $script:projectId + "/ops?since=0")
    (@($r.ops | Where-Object { $_.kind -eq 'note' -and $_.note -eq 'ci note' })).Count -eq 1
}

Check "session wait returns quickly when activity changed" {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Get-Json ("/api/projects/" + $script:projectId + "/session/wait?activity=0&clientId=" + $script:clientId + "&ms=5000")
    $watch.Stop()
    $r.ok -eq $true -and $watch.Elapsed.TotalSeconds -lt 5
}

Check "session leave removes the participant" {
    Post-Json ("/api/projects/" + $script:projectId + "/session/leave") @{ clientId = $script:clientId } | Out-Null
    $r = Get-Json ("/api/projects/" + $script:projectId + "/session")
    (@($r.members | Where-Object { $_.nick -eq 'tester' })).Count -eq 0
}

# ---------------------------------------------------------------- auth
# the gate: loopback never needs a token, other addresses do
$tokenFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\auth.token'
$lanIp = $null
try {
    $lanIp = [System.Net.Dns]::GetHostAddresses([System.Net.Dns]::GetHostName()) |
        Where-Object { $_.AddressFamily -eq 'InterNetwork' -and $_.IPAddressToString -notlike '127.*' } |
        Select-Object -First 1 -ExpandProperty IPAddressToString
} catch { }

if ($lanIp -and (Test-Path $tokenFile)) {
    $remote = "http://" + $lanIp + ":" + $Port

    Check "requests from another address need a token" {
        try { Invoke-RestMethod -Uri ($remote + '/api/status') -UseBasicParsing | Out-Null; $false }
        catch { $_.Exception.Response.StatusCode.value__ -eq 401 }
    }

    Check "the same request with the token succeeds" {
        $token = (Get-Content $tokenFile).Trim()
        (Invoke-RestMethod -Uri ($remote + '/api/status?token=' + $token) -UseBasicParsing).ok -eq $true
    }

    Check "health stays open for monitors" {
        (Invoke-RestMethod -Uri ($remote + '/api/health') -UseBasicParsing).ok -eq $true
    }
} else {
    Skip "token gate" "no non-loopback IPv4 address or no bin\auth.token"
}

# ------------------------------------------------------- build from project
Check "build-project packs project files over the base tree" {
    $exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\ModKit.exe'
    $ws = (Get-Json "/api/status").workspace
    $tree = Join-Path $ws ("ci-base-" + [Guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Path (Join-Path $tree 'PSP_GAME\USRDIR') -Force | Out-Null
    Set-Content -Path (Join-Path $tree 'PSP_GAME\PARAM.SFO') -Value 'PARAM' -NoNewline
    [System.IO.File]::WriteAllBytes((Join-Path $tree 'PSP_GAME\USRDIR\ORIG.BIN'), (New-Object byte[] 2048))

    $bytes = New-Object byte[] 3000
    (New-Object System.Random 7).NextBytes($bytes)
    Invoke-RestMethod -Uri ($Base + "/api/projects/" + $script:projectId + "/upload?path=PSP_GAME/USRDIR/ORIG.BIN") `
        -Method Post -ContentType 'application/octet-stream' -Body $bytes -UseBasicParsing | Out-Null

    $out = Join-Path $ws ($script:projectId + "-ci.iso")
    $r = Post-Json ("/api/projects/" + $script:projectId + "/build") @{ base = $tree; target = $out; label = 'CITEST' }
    $state = ''
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        $state = (Get-Json ("/api/jobs/" + $r.job.id)).job.state
        if ($state -in @('done', 'failed', 'cancelled')) { break }
    }

    $ok = ($state -eq 'done') -and (Test-Path $out)
    $extract = Join-Path $ws ($script:projectId + "-ci-out")
    if ($ok) {
        & $exe iso-extract $out $extract | Out-Null
        $inside = [System.IO.File]::ReadAllBytes((Join-Path $extract 'PSP_GAME\USRDIR\ORIG.BIN'))
        $ok = $inside.Length -eq $bytes.Length
        if ($ok) {
            for ($i = 0; $i -lt $bytes.Length; $i++) { if ($inside[$i] -ne $bytes[$i]) { $ok = $false; break } }
        }
    }

    Remove-Item -Recurse -Force $tree -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
    Remove-Item -Force $out -ErrorAction SilentlyContinue
    $ok -and (-not (Test-Path $tree))
}

# -------------------------------------------------------------------- jobs
Check "operation is queued as a job" {
    $r = Post-Json "/api/ops" @{
        name = 'identify'; project = $script:projectId
        source = 'docs/hello.txt'; target = ''; extra = ''; user = 'ci'
    }
    $script:jobId = $r.job.id
    $r.ok -eq $true -and $r.job.state -in @('queued', 'running')
}

Check "job reaches a final state" {
    $state = ''
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $j = Get-Json ("/api/jobs/" + $script:jobId)
        $state = $j.job.state
        if ($state -in @('done', 'failed', 'cancelled')) { break }
    }
    $state -eq 'done'
}

Check "jobs list contains the job" {
    $r = Get-Json "/api/jobs"
    (@($r.jobs | Where-Object { $_.id -eq $script:jobId })).Count -eq 1
}

Check "unknown job is 404" {
    try { Get-Json "/api/jobs/job-9999" | Out-Null; $false } catch { $_.Exception.Response.StatusCode.value__ -eq 404 }
}

# -------------------------------------------------------------------- logs
Check "logs endpoint returns lines" {
    $r = Get-Json "/api/logs?lines=20"
    $r.ok -eq $true -and $r.lines.Count -ge 1
}

Check "log lines carry level tags" {
    $r = Get-Json "/api/logs?lines=50"
    (@($r.lines | Where-Object { $_ -match '\[(INF|WRN|ERR|VRB|DBG)\]' })).Count -ge 1
}

# ------------------------------------------------------------------ cleanup
Check "delete project" {
    Invoke-RestMethod -Uri ($Base + "/api/projects/" + $script:projectId) -Method Delete -UseBasicParsing | Out-Null
    $r = Get-Json "/api/projects"
    ($r.projects | Where-Object { $_.id -eq $script:projectId }).Count -eq 0
}

Write-Host ""
if ($script:failed -eq 0) {
    Write-Host ("web tests: " + $script:passed + "/" + $script:passed + " passed") -ForegroundColor Green
    exit 0
} else {
    Write-Host ("web tests: " + $script:passed + " passed, " + $script:failed + " failed") -ForegroundColor Red
    exit 1
}