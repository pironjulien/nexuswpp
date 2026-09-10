# Consignes NexusWpp

## Projet

- Application Windows native de fond d'ecran dynamique.
- L'hote principal est `DesktopHtmlHost.cs`.
- L'interface est dans `index.html`, `style.css` et `app.js`.
- Le dossier d'installation local est `C:\nexuswpp`.

## Commandes utiles

```powershell
.\compile.ps1
.\scripts\build_installer.ps1
.\scripts\build_msix.ps1
```

## Verification

- Verifier que `.\compile.ps1` compile `bin\nexuswpp.exe`.
- Verifier le transfert des clics et le cycle de vie du hook avec `.\scripts\test_mouse_hook.ps1` apres une modification de la gestion souris.
- Verifier que `.\scripts\build_installer.ps1` cree `dist\NexusWppSetup.exe`.
- Verifier que `.\scripts\build_msix.ps1` cree le package Store `dist\msix\julienpiron.fr.NexusWpp_1.0.14.0_x64.msix`.
- Ne pas versionner `bin/`, `dist/`, les logs, les fichiers temporaires ou les resultats de benchmark.

## Habitudes projet

- Travailler sur `main`.
- Mettre a jour `CHANGELOG.md` pour chaque changement fonctionnel.
- Pour le Store, garder l'identite MSIX `julienpiron.fr.NexusWpp` et le Publisher `CN=C3E3A6F0-11D2-4EE1-B3F2-34EED4CAE7FA`.
- Garder les messages de commit courts et en francais.
- Ne pas ajouter de faux bouton, de fausse donnee ou de fonction non cablee.
