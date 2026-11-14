# Deltarune-PS4-FR — le patch FR de DELTARUNE sur la version PS4 (GoldHEN)

Un outil Windows qui fabrique, **à partir de tes propres fichiers**, la version PS4 de DELTARUNE en français :
il reporte le [patch FR](https://deltarune-fr.com) de la version PC sur les fichiers de la version PS4.

**Ce dépôt ne contient ni le jeu, ni la traduction.** Il faut ton jeu PS4, ton jeu PC et le patch FR installé sur
ce dernier. Les fichiers fabriqués contiennent le jeu et le travail de l'équipe du patch FR : ils sont pour ta
console, ne les partage pas.

> **État (0.6.0) : ça marche, sur une seule configuration essayée.** PS4 Slim, firmware 10.01, GoldHEN 2.4b18, jeu en
> version 1.12 (chapitres 1 à 5), patch FR de novembre 2026 : l'écran de choix du chapitre et le chapitre 5
> démarrent en français, avec les bordures d'écran. Les autres chapitres sont fabriqués de la même façon mais n'ont
> pas été joués, et rien n'a été joué jusqu'au bout. Les retours sont les bienvenus.

**En français et à 60 FPS :** l'outil [Deltarune-FR-60FPS](https://github.com/Nabiscan58/Deltarune-FR-60FPS) ajoute
le mod [60 FPS Overhaul](https://gamebanana.com/mods/688311) de BadArtAdventure au jeu PC déjà en français. Fais
cette fusion sur PC, puis lance DeltaruneFR-PS4 comme d'habitude : il part du `data.win` de ton jeu PC et fabrique
donc les fichiers PS4 en français et à 60 FPS. Essayé sur la même console (le jeu tourne, il a été peu joué).

Projet de fans, sans lien avec Toby Fox, 8-4, Sony ni l'équipe du patch FR.

## Utilisation

Télécharge `DeltaruneFR-PS4-<version>.zip` dans les
[Releases](../../releases) et suis `LISEZ-MOI.txt` (aussi [dans le dépôt](LISEZ-MOI.txt)). En résumé :

1. **Copier les fichiers du jeu PS4 sur le PC.** Jeu lancé, par FTP (mode binaire), copie
   `/mnt/sandbox/pfsmnt/CUSAxxxxx-app0` et `CUSAxxxxx-patch0` dans un dossier du PC.
2. **Lancer `DeltaruneFR-PS4.exe`**, indiquer ce dossier, le dossier du jeu PC en français et un dossier de sortie.
   Le journal dit ce qui a été trouvé ; « 0 point(s) à regarder » à la fin veut dire que tout s'est passé comme prévu.
3. **Sur la PS4**, activer le plugin AFR de GoldHEN pour le jeu (`/data/GoldHEN/plugins.ini`) et copier le dossier
   de sortie dans `/data/GoldHEN/AFR/`. Le jeu installé n'est pas modifié : retirer les deux lignes de `plugins.ini`
   rend le jeu d'origine.

## Comment ça marche

Pour l'écran de choix du chapitre et pour chaque chapitre, il y a trois fichiers de données GameMaker :

| | fichier | |
|---|---|---|
| PC d'origine | `chapterN_windows/data.win.bak` | gardé par l'installateur du patch FR |
| PC en français | `chapterN_windows/data.win` | le même, avec le patch FR |
| PS4 d'origine | `CUSAxxxxx-patch0/games/chapterN_ps4/game.win` | la même version du jeu, compilée pour la PS4 |

Le fichier PC d'origine et le fichier PS4 sont la même version du jeu compilée pour deux machines. `port/Port.csx`
(un script pour [UndertaleModTool](https://github.com/UnderminersTeam/UndertaleModTool)) trouve ce qui les distingue
et l'applique au fichier PC en français :

- le nom de configuration et les options (`GEN8`, `OPTN`) ;
- les quelques valeurs du code propres à la PS4 (identifiants PSN), instruction par instruction, sans toucher aux
  textes traduits ;
- les extensions absentes sur PS4 : leurs appels deviennent `extension_stubfunc_real`, comme le fait le compilateur
  PS4, et leurs identifiants sont retirés ;
- les shaders compilés (PSSL à la place de HLSL) ;
- les images propres à la console (bordures d'écran, boutons) : elles n'existent pas dans le fichier PC et sont
  reprises du fichier PS4, avec leurs pages de textures et leur groupe de textures.

Rien n'est propre à une version du jeu : tout est trouvé par comparaison et écrit dans le journal.

Le patch FR a été écrit pour le PC. Deux adaptations sont faites pour la console :

- le patch démarre (`scr_drfr_init_localization`, `init_drfr`) seulement là où le jeu PC démarre ; sur console le jeu
  démarre une fois les sauvegardes chargées, et le script y ajoute ce démarrage (fonction `drfr_ps4_gamestart`) ;
- la reprise des réglages d'une ancienne version du patch (`convert_sakushi_flags`), qui lit les sauvegardes avant
  qu'elles soient chargées, est sautée au démarrage.

Les fichiers ajoutés ou remplacés par le patch FR (chansons, vidéos, textes du chapitre 1) sont copiés à côté.

## Limites connues

- **Bordures d'écran** : le patch FR a traduit le nom du réglage (« Dynamic » → « Dynamique »), et celui enregistré
  en anglais n'est plus reconnu. Il suffit de choisir à nouveau la bordure dans Config ; cela peut se reproduire
  d'un chapitre à l'autre.
- L'option « Langue » de l'écran de choix du chapitre peut ne rien faire sur console.
- Les pages d'images ajoutées par le patch FR sont en PNG, alors que le jeu PS4 d'origine n'a que du QOI compressé.
  Les textes français s'affichent sur la configuration essayée ; les images traduites n'ont pas été vérifiées une à une.
- Il faut que le jeu PC et le jeu PS4 soient la même version.

## Contenu du dépôt

| Chemin | Contenu |
|---|---|
| `port/Port.csx` | tout le travail : comparaison, application, journal |
| `port/MakeTest.csx`, `MakeTest23.csx` | fichiers d'essai fabriqués (aucun fichier du jeu) pour tester le script |
| `launcher/DeltaruneFR-PS4.cs` | la fenêtre Windows qui lance UndertaleModCli avec le script |
| `scan/DeltaruneScan.cs` | outil d'analyse : structure des fichiers PS4 et PC dans un rapport texte |
| `build.sh` | fabrique le zip de la release (Linux ou WSL : `mono-mcs`, `python3`, `zip`) |

Sans la fenêtre : `UndertaleModCli load <jeu PC>\data.win -s Port.csx`, avec les variables d'environnement
`DRFR_PS4`, `DRFR_PC` et `DRFR_OUT`.

## Signaler un problème

Ouvre un ticket avec le journal (`deltarune-fr-ps4-journal.txt`), une photo du message d'erreur du jeu et, si
possible, le fichier du chapitre concerné dans `sortie\code-fr\`. **N'envoie jamais de fichiers du jeu, ni les
fichiers fabriqués.**

## Licence et crédits

GPL-3.0 (voir `LICENSE`), comme UndertaleModTool dont le script utilise la bibliothèque et dont le zip de la release
contient l'outil en ligne de commande.

Merci à l'équipe du patch FR de DELTARUNE pour la traduction, à l'équipe d'UndertaleModTool, et à jocover et SiSTR0
pour le plugin AFR de GoldHEN. DELTARUNE est un jeu de Toby Fox.
