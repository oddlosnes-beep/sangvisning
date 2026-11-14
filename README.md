# Sangvisning

Lokalt program for sangtekster, videoer og lysbilder på projektor i møter og gudstjenester.
Kjører på din egen PC, uten sky og uten abonnement. Sangene lagres i `%AppData%\Sangvisning`.

## Installere (Windows)
1. Gå til **Releases** her på GitHub og last ned `Sangvisning-Setup.exe` fra siste versjon.
2. Dobbeltklikk. Windows kan vise «Ukjent utgiver». Trykk **Mer info**, så **Kjør likevel**. Filen er ikke signert ennå.
3. Følg veiviseren. Du får snarvei på skrivebordet. .NET er med i installasjonen.

Sanger og innstillinger blir ikke rørt av en ny installasjon, så du kan installere nye versjoner over.

## For utvikling
```
dotnet run
```
Krever .NET SDK 10. Hver endring i `main` bygger automatisk en ny `Sangvisning-Setup.exe` (GitHub Actions).
