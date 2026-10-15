#!/usr/bin/env bash
# generate.sh
#
# Regenerates the Trivy fixture used by the golden-file tests:
#   trivy-dotnet-publish.spdx.json  Trivy's SPDX for a small .NET publish tree
#   registry-responses.json         recorded NuGet/license-URL responses for it
#
# Mirrors a typical CI job: `dotnet publish` a web app that references an internal
# class library (Contoso.*, stands in for first-party projects), then
# `trivy fs --format spdx-json` on the publish directory. The package set is
# chosen for license variety: MIT, Apache-2.0, BSD-3-Clause, PostgreSQL, a
# SemVer 2.0 prerelease, and two legacy licenseUrl-only packages.
#
# Needs the .NET 10 SDK, trivy, python3 and network access. Only run it when
# the fixture should change; the tests themselves never touch the network.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$WORK/src/Contoso.Billing" "$WORK/src/Contoso.Web"

cat > "$WORK/src/Contoso.Billing/Contoso.Billing.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
EOF
echo 'namespace Contoso.Billing; public static class Invoice { }' > "$WORK/src/Contoso.Billing/Invoice.cs"

cat > "$WORK/src/Contoso.Web/Contoso.Web.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <NoWarn>$(NoWarn);NU1701</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Serilog" Version="4.2.0" />
    <PackageReference Include="Polly.Core" Version="8.5.2" />
    <PackageReference Include="Npgsql" Version="9.0.3" />
    <PackageReference Include="OpenTelemetry.Exporter.Prometheus.AspNetCore" Version="1.15.3-beta.1" />
    <PackageReference Include="Humanizer.Core" Version="2.2.0" />
    <PackageReference Include="Antlr" Version="3.5.0.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Contoso.Billing/Contoso.Billing.csproj" />
  </ItemGroup>
</Project>
EOF
echo 'WebApplication.Create(args).Run();' > "$WORK/src/Contoso.Web/Program.cs"

# Relative paths keep the developer's absolute path out of the SPDX document.
(cd "$WORK" && dotnet publish src/Contoso.Web -c Release -o publish --nologo -v q)
(cd "$WORK" && trivy fs --quiet --format spdx-json --output trivy.spdx.json publish)

cp "$WORK/trivy.spdx.json" "$HERE/trivy-dotnet-publish.spdx.json"
python3 "$HERE/../record-registry.py" "$HERE/registry-responses.json" "$HERE/trivy-dotnet-publish.spdx.json"

echo "Fixture regenerated with $(trivy --version | head -n1). Review the golden-file diffs."
