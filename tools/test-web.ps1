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