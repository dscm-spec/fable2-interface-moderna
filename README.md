# Fable 2 Return to Albion — Interface Moderna v1 (teclado e mouse)

Mod para a DLL do launcher do **Refii** (Fable 2 Return to Albion). Deixa os menus mais
confortáveis no teclado e mouse e mais rápidos, sem mudar a arte original do jogo.

Vídeo: veja `Fable 2 - interface moderna v1.mp4`.

## O que muda

- **Destaque ao passar o mouse** — a linha sob o mouse fica branca, cresce um pouco e desliza,
  sem roubar a seleção do teclado.
- **"Esc Voltar" clicável** — acende com o mouse em cima; clicar volta, igual ao Esc.
- **Home / End** — pulam para o começo/fim das listas.
- **Aro de seleção no teclado/mouse** — o aro dourado original aparece também fora do controle,
  mostrando a tecla certa (E) em vez do botão A.
- **"(Equipado)" / "(Equipped)"** — marca a arma e a roupa em uso nas listas.
- **Ponteiro de manopla** — troca a seta do Windows por uma manopla, ajustada à resolução.
  Variações de cor em `ponteiro/variacoes/`.
- **Roda social** — Q aperta abre, aperta fecha (não precisa segurar); escolhe com o mouse.
- **Menus mais rápidos** — listas e menu principal abrem sem os travões de antes; materiais do
  mundo copiados sem ida ao render thread.
- **Textos do menu de pausa** — "Sair para a área de trabalho", "Salvar e sair para a tela de
  título" etc. aparecem em português **só** se a tradução PT-BR estiver instalada; em inglês o
  jogo continua em inglês.

## Instalação

1. **Feche o jogo.**
2. Vá em `Fable 2 Return to Albion\data_refii-launcher_windows_x86_64\`.
3. **Faça backup** de `refii-launcher.dll` e `refii-launcher.pdb` (ex.: renomeie para `.original`).
4. Copie `dll\refii-launcher.dll` e `dll\refii-launcher.pdb` para essa pasta.
5. Copie `ponteiro\refii_cursor.png` e `ponteiro\refii_cursor_hotspot.txt` para a pasta do
   executável do jogo (onde fica `Fable 2 Return to Albion.exe`).

Para trocar a cor da manopla, copie um arquivo de `ponteiro\variacoes\` por cima de
`refii_cursor.png` (mantendo esse nome).

## Desligar partes (sem desinstalar)

Crie um arquivo vazio com o nome abaixo na pasta do executável:

| Arquivo | Desliga |
|---|---|
| `refii_hover_off.flag` | destaque do mouse e o "Esc Voltar" clicável |
| `refii_cursor_off.flag` | a manopla (volta a seta do Windows) |
| `refii_equipped_tag_off.flag` | o "(Equipado)" |
| `refii_wheel_hold.flag` | roda social volta a ser "segurar Q" |
| `refii_kbm_cursor_off.flag` | o aro de seleção no teclado/mouse |

## Desinstalar

Feche o jogo e devolva os dois arquivos do backup. Apague `refii_cursor.png` e
`refii_cursor_hotspot.txt` se quiser.

## Compatibilidade

- Feito sobre a mesma versão do Refii que acompanha o Return to Albion atual. Se o seu Refii for
  de outra versão, **não use** esta DLL (ela substitui o launcher inteiro).
- Não mexe em saves nem no `refii.toml`.
- Funciona com ou sem a tradução PT-BR.

## Créditos

- Refii (recompilação do Fable 2) — seus autores.
- Manopla: "gauntlet cursor", OpenGameArt.org, domínio público (CC0).
- Código-fonte das mudanças em `codigo/` (patch + arquivos alterados).
