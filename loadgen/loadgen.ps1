# Synthetic traffic for books-dotnet. Runs for -DurationSeconds then exits; the
# scheduled task (register-loadgen.ps1) starts it again every few minutes.
#
# Mix: mostly reads, a create/update/delete cycle, plus a few deliberate client
# errors (404, 400 validation, 409 duplicate ISBN) so the error metrics and
# span statuses have something to show.
param(
    [string]$BaseUrl = 'http://127.0.0.1:8080',
    [int]$DurationSeconds = 290,
    [int]$PauseMs = 1000
)
$ErrorActionPreference = 'Continue'

function Call([string]$Method, [string]$Path, $Body) {
    $req = @{ Method = $Method; Uri = "$BaseUrl$Path"; UseBasicParsing = $true; TimeoutSec = 30; SkipHttpErrorCheck = $true }
    if ($null -ne $Body) { $req.Body = ($Body | ConvertTo-Json -Compress); $req.ContentType = 'application/json' }
    try {
        $r = Invoke-WebRequest @req
        if ($r.Content) { $r.Content | ConvertFrom-Json -ErrorAction SilentlyContinue } else { $null }
    } catch { Write-Warning "$Method $Path failed: $($_.Exception.Message)" }
}

function NewIsbn { '979-' + (Get-Random -Minimum 1000000000 -Maximum 9999999999) }

$deadline = (Get-Date).AddSeconds($DurationSeconds)
while ((Get-Date) -lt $deadline) {
    $roll = Get-Random -Maximum 100
    switch ($roll) {
        { $_ -lt 35 } { Call GET '/books' | Out-Null; break }
        { $_ -lt 60 } {
            $books = @(Call GET '/books')
            if ($books.Count) { Call GET "/books/$(($books | Get-Random).id)" | Out-Null }
            break
        }
        { $_ -lt 70 } { Call GET '/healthz' | Out-Null; break }
        { $_ -lt 75 } { Call GET '/test' | Out-Null; break }
        { $_ -lt 88 } {
            # create -> read -> update -> delete
            $book = Call POST '/books' @{
                title = "Load Test Book $(Get-Random -Maximum 100000)"; author = 'loadgen'
                isbn = NewIsbn; description = 'Synthetic book from loadgen'; price = [math]::Round((Get-Random -Minimum 5.0 -Maximum 80.0), 2)
            }
            if ($book.id) {
                Call GET "/books/$($book.id)" | Out-Null
                Call PUT "/books/$($book.id)" @{ price = [math]::Round((Get-Random -Minimum 5.0 -Maximum 80.0), 2) } | Out-Null
                Call DELETE "/books/$($book.id)" | Out-Null
            }
            break
        }
        { $_ -lt 92 } { Call GET "/books/$(Get-Random -Minimum 900000 -Maximum 999999)" | Out-Null; break }   # 404
        { $_ -lt 96 } { Call POST '/books' @{ title = 'Incomplete'; author = 'loadgen' } | Out-Null; break }   # 400
        default {
            # 409: reuse an existing ISBN
            $books = @(Call GET '/books')
            if ($books.Count) {
                Call POST '/books' @{ title = 'Dup'; author = 'loadgen'; isbn = $books[0].isbn; description = 'dup'; price = 1 } | Out-Null
            }
        }
    }
    Start-Sleep -Milliseconds (Get-Random -Minimum ($PauseMs / 2) -Maximum ($PauseMs * 3 / 2))
}
