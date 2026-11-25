# Guia dos mods do Raft (para trabalhar com outra IA)

Este ficheiro explica tudo o que precisas de saber para criar ou alterar mods deste projeto.
Dá-o inteiro à IA no início da conversa.

---

## 1. O que é o projeto

- Jogo: **Raft** (Unity, Mono), com o **Raft Mod Loader (RML)**.
- Os mods são ficheiros `.rmod` = um zip com `modinfo.json` + **um único ficheiro `.cs`**.
  O RML compila o `.cs` dentro do jogo.
- Pasta do projeto: `C:\Users\Deyvi\Desktop\PLUGIN\raft-mods`
- Pasta dos mods do jogo: `C:\Users\Deyvi\Desktop\JogosCracked\Raft.Gamdie.com\Raft\mods`
- Os mods são publicados em **https://bestcode.pt/raft/** e o mod **Mod Sync** de cada jogador
  descarrega as versões novas sozinho e recarrega-as com o jogo aberto.

```
raft-mods/
  src/<pasta-do-mod>/<Nome>.cs        <- o código (1 só .cs por pasta)
  src/<pasta-do-mod>/modinfo.json     <- nome, versão, descrição
  src/bcui_kit.cs.txt                 <- kit de UI partilhado (ver secção 6)
  build.ps1     <- compila 1 mod, instala e publica
  update.ps1    <- compila TODOS os que mudaram e publica (usa este)
  publish.ps1   <- só publica o que está na pasta mods do jogo
  PARA_O_PEDRO.md  <- descrição dos mods para os jogadores
```

## 2. Mods existentes

| Pasta | Ficheiro | Versão | O que faz |
|---|---|---|---|
| `src/calmfishing` | CalmFishing.cs | 1.7.0 | Cardumes, gaivotas, tempestades |
| `src/stackchests` | StackableChests.cs | 1.1.0 | Baús empilháveis |
| `src/raftlife` | RaftLife.cs | 1.1.0 | Vida na jangada; tubarão ferido foge |
| `src/fishfight` | FishFight.cs | 1.3.1 | Pesca com luta (tensão, linha, peixe foge) |
| `src/skills` | Skills.cs | 1.2.0 | 6 skills com níveis (painel canto inferior direito, tecla K) |
| `src/inspect` | Inspect.cs | 1.2.1 | Automático (canto superior esquerdo) ao olhar para grelhas, purificadores, fundições, panela, espremedor, hortas e depósitos: janela com o estado |
| `src/leatherui` | LeatherUI.cs | 1.7.0 | Couro e Papel: reskin dos menus do jogo (crafting, inventário, botões) em couro e papel. Texturas pintadas em código (classe SkinPaint, uma SkinSpec por sprite do jogo, pelo nome); troca em memória e repõe ao descarregar. Com o inventário aberto (CanvasHelper.ActiveMenu == Inventory) mostra o mesmo livro do diário (T) atrás dos menus (BC_Book, antes de "Crafting" em _CanvasGame_New): véu 0.42 + vinheta, capa de couro com grão e costura, páginas de papel envelhecido, lombada, títulos FABRICO / INVENTÁRIO com régua dupla e losango (arte portada do LbArt do Logbook). Layout() corre todos os frames: o livro ajusta-se ao canvas e fica acima da Hotbar; página esquerda = coluna de categorias + lista (CraftingBox_Category + "CraftingBox_Selected Category", que o jogo alarga a 315+55*(itens-3)); a borda desenhada dos botões de categoria (colInset, medido em MeasureInv) alinha com o título FABRICO (PadOut); o Crafting fica SEMPRE à escala 1 (o Pedro rejeitou escalar por categoria); FitList() fixa a largura da lista até à lombada e a altura até ao pé da página (scroll só se não couber) e FitRow() quebra em várias linhas as linhas de receita demasiado compridas (desliga o HLG/CSF do SubItems e posiciona os filhos com a fórmula do HLG; tudo reposto ao fechar). Página direita: MeasureInv() mede o que o inventário desenha mesmo (o rect é mais alto que os slots; texto medido pelos vértices das letras, porque os "Slot number" do equipamento têm rects de 499x516; ignora alfa herdado de CanvasGroup; imagens Simple medidas pela caixa que o sprite desenha mesmo, com padding e preserveAspect, como Image.GetDrawingDimensions; texto cortado ao rect do pai; graficos cortados por mascara (canvasRenderer.cull ou fora do RectMask2D) nao contam - o cartao guarda linhas de custo antigas escondidas; filhos do InventoryParent que nao desenham nada sao ignorados (o rect vazio empurrava o inventario ~95 para cima); Layout escreve no Player.log "[Couro e Papel] layout: ..." com as medidas e o grafico que define topo/fundo); desde 1.5.0 o cartão da receita (SelectedItemBox) fica no pé da página ESQUERDA, por baixo da lista, com a largura da coluna da lista (escala 1..2.2, até 45% da altura da página) e a lista faz scroll no espaço acima; a página direita é só o inventário, logo abaixo do cabeçalho (até 2.2x, passos de 0.05, medido pela parte desenhada cardVis, que o PlaceCard alinha ao topo); Canvas aninhados com overrideSorting (o "Equipslot parent" dos slots de equipamento) ficam com overrideSorting=false enquanto o livro está aberto, senão ficavam por baixo das páginas. Sem crafting (mesa de pesquisa) o inventário fica no topo. Desde 1.6.3, com um baú aberto (filho Inventory_Storage_* do InventoryParent) o Crafting inteiro (categorias, lista, cartão) fica escondido por CanvasGroup (alfa 0, sem raycasts; reposto ao fechar), o baú vai centrado na página esquerda (storeShift) e o inventário centrado na direita; 1.6.4: o topo do baú alinha com o do inventário, a página esquerda passa a BAÚ / Arrumação e mantimentos e a caixa castanha do baú (ChestBox) fica a alfa 0; 1.6.5: BrownBackground/DarkShadow do baú escondidos pelo nome e o texto do baú (nome editável, desbotado) a tinta opaca; 1.6.6: baú e inventário encostados à lombada (70 de cada lado); 1.7.0: imagem, nome e descrição do item selecionado (PlayerInventory.itemImage/itemNameText/itemDescriptionText) escondidos por CanvasGroup enquanto o livro está aberto. Fundos MenuBackground_9Slice/Box1 a alfa 0, texto claro passa a tinta e perde a sombra; tudo reposto ao fechar |
| `src/quicksave` | QuickAutosave.cs | 1.3.1 | Autosave configurável + contador |
| `src/bigpalms` | BreakableBigPalms.cs | 1.0.2 | Palmeiras grandes partem-se |
| `src/treefix` | TreeHarvestFix.cs | 1.0.0 | Correção na apanha das árvores |
| `src/shark` | Shark_Attack_Delayed.cs | 1.2.2 | Tubarão ataca mais tarde |
| `src/hardcore` | HardcoreSurvival.cs | 2.6.4 | HUD estilo Minecraft (MineHud, desde 2.6.0): por cima da hotbar, dividido ao meio; à esquerda a vida (alinhada à borda esquerda da hotbar) com os ícones do equipamento vestido por cima (equipSlots, fundo StatIconBG); à esquerda por cima da vida (2.6.3: energia, calor e saúde empilhados no canto inferior esquerdo do ecrã, 16px da borda, medidos pelo rootCanvas; 2.6.4: equipamento à direita da barra do sono, calor sem piscar, fome castanho-carne e sede azul-água via Slider.fillRect); à direita fome e sede empilhadas (alinhados à borda direita); a barra de oxigénio fica sempre escondida (CanvasGroup alfa 0); filas nativas identificadas pelo sprite StatIcon_*, soltas do layout com LayoutElement.ignoreLayout; tudo reposto em Kill. Sem estado molhado/encharcado (desde 2.5.5 wet fica sempre 0: a água só arrefece enquanto estás nela). Calor só junto a fontes a 3 m (grelhas, filtros, fundições, lanterna, cesto de fogo, lareira, banheira; lanternas/fogos aquecem sempre); molhado tem de secar primeiro; roupa de couro isola bem (também na água); água arrefece devagar. Sono só nos últimos 20% da barra de energia (57.6 h acordado de 72). Intoxicação por comer cru. SEM febre (removida, sem filtros nem marcador). Barra de calor com marcador a 35% (frio). Cartão do amigo: dados por rede (slug hardcore-survival, envio fiável) ou vida/fome/sede do jogo |
| `src/modsync` | ModSync.cs | 2.1.4 | Atualiza os mods de todos a partir do site |

## 3. Fluxo de trabalho

1. Edita o `.cs` em `src/<pasta>/`.
2. **Sobe a versão nos dois sítios** (têm de ser iguais, senão o build falha):
   - no código: `public const string Version = "1.3.2";`
   - no `modinfo.json`: `"version": "1.3.2"`
3. No PowerShell, dentro de `raft-mods`:
   ```powershell
   update -NoPublish   # compila e instala só no meu PC para testar
   update              # compila o que mudou e publica para todos
   update -All         # recompila tudo
   ```
4. Testar no jogo. Consola do jogo: **F10**.
   Log: `%USERPROFILE%\AppData\LocalLow\Redbeet Interactive\Raft\Player.log`
   (procura `[NomeDoMod]` ou `error`).

**Mod novo:** copia uma pasta de `src/`, muda o `name`, `version` e `description` no `modinfo.json`,
renomeia a classe/ficheiro `.cs`, corre `update`. O `.rmod` é criado com o nome do mod em minúsculas.

`update` tem de correr no **PowerShell** (não no bash/Git Bash: a publicação falha).

## 4. Regras obrigatórias do código (senão não compila no jogo)

- **C# 5 apenas.** PROIBIDO:
  - `$"texto {x}"` -> usar `"texto " + x`
  - `?.` e `??=` -> usar `if (x != null)` (`??` simples é permitido)
  - `nameof(...)`, `=>` em propriedades/métodos (expression-bodied), `out var`, tuplos `(a, b)`,
    `is Tipo t` (pattern matching), funções locais, `default` sem tipo, inicializadores `{ get; } = x`
- **Um só ficheiro `.cs`** por mod. Tudo (classes auxiliares, UI, patches) vai dentro dele.
- A classe principal herda de `Mod`:
  ```csharp
  using System;
  using UnityEngine;
  using HarmonyLib;
  using HMLLibrary;
  using RaftModLoader;

  public class MeuMod : Mod
  {
      public const string Version = "1.0.0";
      Harmony harmony;

      public void Start()
      {
          harmony = new Harmony("com.bestcode.meumod");
          harmony.PatchAll(typeof(MeuMod).Assembly);
          Debug.Log("[MeuMod] carregado v" + Version);
      }

      public void OnModUnload()
      {
          if (harmony != null) harmony.UnpatchAll("com.bestcode.meumod");
          // destruir tudo o que o mod criou (UI, GameObjects) e repor valores alterados
      }

      [ConsoleCommand("meumod", "Descrição. Sintaxe: meumod [on|off]")]
      public static string Cmd(string[] args) { return "ok"; }
  }

  [HarmonyPatch(typeof(Axe), "OnAxeHit")]
  static class P_Axe
  {
      static void Postfix(Axe __instance) { /* ... */ }
  }
  ```
- **`OnModUnload` tem de limpar tudo** (patches, UI, valores mudados), porque o Mod Sync recarrega
  os mods com o jogo aberto.
- Membros **privados** do jogo podem ser usados diretamente (o RML compila contra um
  Assembly-CSharp "publicizado"), ex.: `rod.playerNetwork`, `hook.gatherTimer`.
- Mods diferentes são assemblies separados: um mod não pode referenciar a classe de outro
  diretamente. Usar **reflexão** (ver o Skills a ligar-se ao Fish Fight:
  `a.GetType("FishFight+Fight")` + `harmony.Patch(...)`).
- Jogador local: `RAPI.GetLocalPlayer()` (devolve `Network_Player`, null no menu).
  Inventário: `player.Inventory.AddItem("Plank", 2)`. Estado do menu:
  `CanvasHelper.ActiveMenu != MenuType.None`.
- Teclado: `UnityEngine.InputSystem.Keyboard.current.kKey.wasPressedThisFrame`.
- Mundo atual: `SaveAndLoad.CurrentGameFileName`. Host: `Raft_Network.IsHost`.
- Enum `SubmersionState` está em `UltimateWater.SubmersionState`.
- Texto visível para o jogador em **português de Portugal**, **sem emojis**.
  O UI tem de parecer nativo do Raft (usar o kit).

## 5. Ver o código do jogo (descompilar)

Para descobrir que métodos fazer patch:
```powershell
dotnet tool install -g ilspycmd   # 1x
$M = 'C:\Users\Deyvi\Desktop\JogosCracked\Raft.Gamdie.com\Raft\Raft_Data\Managed\Assembly-CSharp.dll'
ilspycmd -t FishingRod $M > FishingRod.cs      # uma classe
ilspycmd -l c $M | Select-String Hook          # procurar classes por nome
```
Pontos de patch já usados (bons exemplos):
- Pesca: `FishingRod.PullItemsFromSea`, `FishingRod.Update`
- Anzol: `Hook.HandleGathering`, `Hook.gatherTime`, `Throwable.Animation_HookThrow` (`lastThrowForce`),
  `ItemCollector.AddCollectedItemsToPlayer`
- Árvores: `Axe.OnAxeHit`, `HarvestableTree.Harvest(PlayerInventory)`
- Dano/animais: `Network_Entity.Damage(float, Vector3, Vector3, EntityType, bool isLocal, SO_Buff)`, `IsDead`
- Oxigénio: `Stat_Oxygen.SetOxygenLostPerSecond / GetOxygenLostPerSecond`
- Plantas: `PlantManager.Harvest(Plant, bool giveYield)`
- Autosave: `SaveAndLoad` (campos de delay/timer)
- Comer: `PlayerStats.Consume(Item_Base)`; `item.settings_consumeable.IsRaw`
- Cama/hora: `BedManager.Slumbering`, `player.BedComponent.Sleeping`; a hora (`AzureSkyController.timeOfDay.hour`) lê-se por reflexão a partir de `BedManager.skyController`
- Tempo: `WeatherManager.GetCurrentWeatherType()` (Calm, Fog, BigWaves, Rain, Snow, SnowRough)
- Fogueiras: `Block_CookingStand.fuel.GetFuelCount() > 0`
- Velocidade: `PersonController.normalSpeed/sprintSpeed/swimSpeed` (guardar o original e repor no unload)

## 6. Kit de UI (estilo nativo do Raft)

`src/bcui_kit.cs.txt` desenha com a fonte e sprites do próprio Raft num canvas partilhado
(`BC_UICanvas`, 1920x1080). **Cada mod cola uma cópia no fim do seu `.cs`**, trocando
`BCUI_NS` por um namespace só seu (ex.: `BCUI_MM`). Os `using` ficam DENTRO do namespace.

API principal (`BCUI_XX.Kit`):
- Cores: `Kit.Cream, Brown, Dark, Bad, Good, Parchment`; `Kit.A(cor, alpha)`
- Âncoras: `Kit.TL, TR, ML, MR, C`
- `Kit.Root()` -> Transform do canvas; `Kit.Node(parent, nome)` -> RectTransform
- `Kit.Place(rt, anchor, pivot, pos, size)`, `Kit.Stretch(rt, l, b, r, t)`
- `Kit.Img(parent, nome, sprite, cor, sliced)`, `Kit.Txt(parent, nome, texto, tamanho, cor, TextAnchor, sombra)`
- `Kit.Plate(rt)` -> placa de pergaminho do jogo; `Kit.Chip(parent, "K", altura)` -> tecla
- `Kit.Get("NomeSprite")`, `Kit.GameFont()`, `Kit.EaseOut(k)`, `Kit.Back(k)`
- Avisos no topo: `BCUI_XX.Toast.Show("TÍTULO", "subtítulo ou null", spriteOuNull, false, 4f)`;
  `Toast.ClearAll()` no `OnModUnload`.
- Para listas no aviso usar `"\n"` (uma linha por item).

Exemplos completos de UI: `Skills.cs` (classe `SkillHud`) e `FishFight.cs` (classe `FishHud`).

## 7. Cuidados

- `modinfo.json`: `"requiredByAllPlayers": true` só se o mod mexer em coisas sincronizadas em
  multiplayer.
- Nunca deixar `out.dll` ou `.rmod` soltos em `src/` (o `update` já limpa).
- O meu PC é a origem: o Mod Sync nunca sobrescreve a pasta mods deste PC.
- Se o Mod Sync partir, os jogadores deixam de receber updates: testar sempre com
  `update -NoPublish` antes de publicar uma versão nova do Mod Sync.
- Depois de mudar um mod, atualizar a linha dele em `PARA_O_PEDRO.md`.

## 8. Ideias de balanço atuais (para não estragar)

- **Fish Fight**: segurar clique enrola; o peixe puxa de tempos a tempos (largar nessa altura).
  Ao morder há 1-3 s para começar a enrolar. Durante a luta, tensão a zero fora de um puxão =
  o peixe foge cada vez mais rápido (`1.2 * exp(1.7 * slack)` m/s).
  Avanço por ciclo: peixe pequeno ~4.8 m, grande ~0.75 m. Mexer em `Reel`, `Take`, `Gap`,
  `PullTime` muda isto tudo.
- **Skills**: nível máximo 20, XP para o próximo nível = `40 + 30 * nivel^1.35`.
  Guardado por mundo em `%USERPROFILE%\AppData\LocalLow\Redbeet Interactive\Raft\BestCodeSkills\<mundo>.txt`.
  Pescador baixa a dificuldade do Fish Fight (até -0.4) e dá 10-40 XP conforme o tamanho do peixe.
  Comandos: `skills`, `skills xp <skill> <n>`, `skills reset`.
