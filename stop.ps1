<#
.SYNOPSIS
    Para o ambiente do Nemus.

.DESCRIPTION
    Como a fase 1 nao tem aplicacao rodando, a unica coisa que fica no ar
    depois do start.ps1 e o servico do PostgreSQL. E ele que este script para.

    Os dados continuam no disco: parar o servico nao apaga banco nenhum.
    Rodar .\start.ps1 de novo devolve tudo como estava.

.PARAMETER PgVersion
    16 ou 18. Padrao 16.

.PARAMETER All
    Para as duas instalacoes (16 e 18), se ambas estiverem no ar.

.EXAMPLE
    .\stop.ps1

.EXAMPLE
    .\stop.ps1 -All
#>

[CmdletBinding()]
param(
    [ValidateSet('16', '18')]
    [string] $PgVersion = '16',

    [switch] $All
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (Test-Path Variable:\PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

function Write-Step { param([string] $Text) Write-Host "`n=> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "   $Text" -ForegroundColor Green }
function Write-Info { param([string] $Text) Write-Host "   $Text" -ForegroundColor DarkGray }

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Stop-PostgresService {
    param([Parameter(Mandatory)] [string] $Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue

    if (-not $service) {
        Write-Info "$Name nao existe nesta maquina; ignorando."
        return
    }

    if ($service.Status -eq 'Stopped') {
        Write-Ok "$Name ja estava parado"
        return
    }

    if (Test-Administrator) {
        Stop-Service -Name $Name
    }
    else {
        Write-Info 'Parar servico exige elevacao; vai aparecer um prompt do UAC.'

        $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru `
            -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', "Stop-Service -Name '$Name'"

        if ($process.ExitCode -ne 0) {
            throw "Nao foi possivel parar o servico $Name (codigo $($process.ExitCode))."
        }
    }

    Write-Ok "$Name parado"
}

# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '  NEMUS - parando ambiente' -ForegroundColor White

try {
    $targets = if ($All) { @('postgresql-x64-16', 'postgresql-x64-18') }
               else      { @("postgresql-x64-$PgVersion") }

    Write-Step 'PostgreSQL'
    foreach ($name in $targets) {
        Stop-PostgresService -Name $name
    }

    Write-Host ''
    Write-Host '  Tudo parado. Os bancos continuam no disco.' -ForegroundColor Green
    Write-Host '  Rodar .\start.ps1 devolve o ambiente como estava.' -ForegroundColor DarkGray
    Write-Host ''

    exit 0
}
catch {
    Write-Host ''
    Write-Host "  FALHOU: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    exit 1
}
