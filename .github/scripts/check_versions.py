#!/usr/bin/env python3
"""Fail if the Jellyfin version the plugin is compiled against differs from the targetAbi it declares.

Checks that these all name the same Jellyfin version (e.g. 10.11.10):
  - src/Jellyfin.Plugin.MediaVote/Jellyfin.Plugin.MediaVote.csproj  <JellyfinPackageVersion>
  - tests/.../Jellyfin.Plugin.MediaVote.Tests.csproj                Jellyfin.Controller / Jellyfin.Model versions
  - build.yaml                                                     targetAbi (must be <version>.0)
  - src/.../Core/ErrorText.cs                                       BuiltForJellyfin
Compile-time and runtime Jellyfin versions must match: 1.0.0.0 was compiled against 10.11.0 and broke on
10.11.10 because 10.11.10 removed IUserManager.Users.
"""
import re
import sys

def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()

errors = []
csproj = read("src/Jellyfin.Plugin.MediaVote/Jellyfin.Plugin.MediaVote.csproj")
m = re.search(r"<PropertyGroup Condition=\"'\$\(JellyfinTarget\)' == '10\.11'\">.*?<JellyfinPackageVersion>([^<]+)</JellyfinPackageVersion>", csproj, re.S)
if not m:
    sys.exit("could not find JellyfinPackageVersion for the 10.11 target in the csproj")
pkg = m.group(1).strip()

yaml = read("build.yaml")
abi = re.search(r'^targetAbi:\s*"([^"]+)"', yaml, re.M).group(1)
if abi != pkg + ".0":
    errors.append(f"build.yaml targetAbi is {abi} but the csproj builds against Jellyfin {pkg} (expected targetAbi \"{pkg}.0\")")

tests = read("tests/Jellyfin.Plugin.MediaVote.Tests/Jellyfin.Plugin.MediaVote.Tests.csproj")
for name in ("Jellyfin.Controller", "Jellyfin.Model"):
    tm = re.search(rf'Include="{re.escape(name)}" Version="([^"]+)"', tests)
    if not tm or tm.group(1) != pkg:
        errors.append(f"tests reference {name} {tm.group(1) if tm else '?'} but the plugin builds against {pkg}")

err = read("src/Jellyfin.Plugin.MediaVote/Core/ErrorText.cs")
bm = re.search(r'BuiltForJellyfin = "([^"]+)"', err)
if not bm or bm.group(1) != pkg:
    errors.append(f"ErrorText.BuiltForJellyfin is {bm.group(1) if bm else '?'} but the plugin builds against {pkg}")

if errors:
    print("Jellyfin version mismatch:")
    for e in errors:
        print("  - " + e)
    sys.exit(1)
print(f"OK: compiled against Jellyfin {pkg}, targetAbi {abi}")
