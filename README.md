# Losket

Mod KSP 1.12.5 ajoutant des marques persistantes sur les pièces : brûlures de
rentrée atmosphérique et poussière soulevée par les moteurs.

## Environnement

| Outil | Version | Emplacement |
|---|---|---|
| KSP (dev) | 1.12.5 build 03190 | `C:\KSPDev\KSP-1.12.5-Losket` |
| KSP (test compat) | 1.12.5 + ~90 mods | install Steam |
| Unity | 2019.4.18f1 | `C:\Program Files\Unity\Editor` |
| .NET SDK | 8.0.424 | `C:\Program Files\dotnet` |

L'install de dev ne contient que Squad, SquadExpansion, ModuleManager, Harmony
et Shabby. Elle sert au développement quotidien. L'install Steam moddée sert
uniquement aux tests de compatibilité (Deferred, TexturesUnlimited, Parallax…).

Pour pointer vers une autre install KSP, créer un `LocalSettings.props` à la
racine (ignoré par git) :

```xml
<Project>
  <PropertyGroup>
    <KSPRoot>D:\Mon\Autre\KSP</KSPRoot>
  </PropertyGroup>
</Project>
```

## Arborescence

```
Losket/
├─ Source/Losket/        code C# du plugin
├─ Unity/LosketShaders/  projet Unity, uniquement pour compiler les shaders
├─ GameData/Losket/      ce qui est copié dans KSP (sortie de build incluse)
├─ Tools/                scripts de build et de lecture de log
├─ Exemples/             références visuelles et cahier des charges
└─ shabby/               copie de référence du dépôt Shabby
```

## Cycle de développement

### Code C#

```bash
powershell -File Tools/build.ps1 -Run
```

Compile, copie `GameData/Losket` dans l'install de dev, et lance KSP. Sans
`-Run`, le jeu n'est pas démarré. La DLL est produite directement dans
`GameData/Losket/Plugins/Losket.dll`.

### Shaders

1. Ouvrir `Unity/LosketShaders` dans Unity 2019.4.18f1.
2. Menu **Losket > Configurer le projet** (une seule fois, après le premier
   ouverture du projet).
3. Menu **Losket > Compiler le bundle de shaders** (`Ctrl+Shift+B`).

Le bundle est écrit dans `GameData/Losket/Shaders/Losket.shaderbundle`. Il faut
ensuite relancer `Tools/build.ps1` pour le déployer.

### Lire les logs

```bash
powershell -File Tools/logtail.ps1
```

Filtre `KSP.log` sur `[Losket]` et les exceptions. `-Follow` suit le fichier en
direct pendant que le jeu tourne.

## Notes techniques

**Chargement du bundle.** Le bundle utilise l'extension `.shaderbundle` et non
`.shab`, et il est chargé par `LosketBootstrap` avec `AssetBundle.LoadFromFile`.
Passer par Shabby est possible mais crée un conflit : Shabby enregistre un
chargeur pour `.shab`, et Unity refuse qu'un même fichier soit chargé deux fois
en tant qu'AssetBundle. Charger nous-mêmes supprime aussi toute dépendance
obligatoire à Shabby.

**Ne pas alléger `Packages/manifest.json`.** Le projet Unity doit conserver le
manifeste par défaut d'Unity 2019.4 et ses 38 modules. Un manifeste réduit
produit un AssetBundle d'apparence parfaitement valide — en-tête UnityFS
correct, version de sérialisation 21, cible `StandaloneWindows64`, mêmes
dépendances externes qu'un bundle qui fonctionne — que KSP refuse ensuite avec
un message trompeur :

> The AssetBundle … could not be loaded because it is not compatible with this
> newer version of the Unity runtime.

Le module critique est `com.unity.modules.assetbundle`. `BuildBundle` vérifie
désormais sa présence avant de compiler et refuse de produire un bundle sans
lui. Aucun symptôme n'apparaît côté Unity : le projet s'ouvre, le shader
compile, le build réussit.

**Espace colorimétrique.** `PlayerSettings.colorSpace` du projet Unity doit
correspondre à celui de KSP. KSP 1.12.5 tourne en **Gamma** (vérifié dans le
journal de démarrage), le projet est réglé en conséquence. La valeur réelle est
journalisée à chaque lancement par `LosketBootstrap`.

**Où lire les journaux.** KSP écrit dans `KSP.log` à la racine du jeu *et* dans
`%USERPROFILE%\AppData\LocalLow\Squad\Kerbal Space Program\Player.log`. Le
premier peut rester figé sur une ancienne session ; `Tools/logtail.ps1` prend
automatiquement le plus récent des deux et affiche lequel il a retenu. Tant que
le jeu tient un journal ouvert, ni sa taille ni sa date d'entrée de répertoire
ne sont rafraîchies — il faut lire le flux, pas les métadonnées.

**APIs graphiques.** Le bundle est compilé pour Direct3D11 et OpenGLCore. Sans
OpenGLCore, les shaders apparaissent en magenta pour les joueurs qui lancent KSP
avec `-force-glcore`.
