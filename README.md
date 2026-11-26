![Capa da Bússola do Loot, com a bússola e o baú dourado de Caçadores de Tesouro sobre a ilha em 3D](docs/imagens/capa.jpg)

# Bússola do Loot

**Conte seus baús no Fortnite, confira o que o replay registrou do seu esquadrão e planeje a próxima rota em Crossbones Isle.** Aplicativo para Windows x64, em português, por **master-yks**.

## Baixar e instalar

1. Na página do repositório, abra **Releases** e escolha uma das opções em **Assets**:
   - **Instalador Windows (recomendado):** baixe **BussolaDoLoot-Setup-win-x64.exe** e execute. A instalação cria os atalhos no Menu Iniciar e na Área de Trabalho e registra o desinstalador no Windows.
   - **Versão portátil (sem instalação):** baixe **BussolaDoLoot-Portable-win-x64.zip**, extraia em uma pasta e abra **BussolaDoLoot.exe** (executável único, sem centenas de arquivos soltos). Opcionalmente, execute **Criar atalhos.cmd**.
2. O runtime .NET 10 já acompanha ambos os pacotes.
3. Ative a gravação de replays no Fortnite e conclua uma partida pública de **Battle Royale** ou **Construção Zero**. A sincronização também pode ser iniciada pelo botão **Sincronizar** do painel.
4. Em **Visão Geral**, escolha o período e veja seus baús por tipo. No **Histórico**, passe o mouse sobre uma partida para conferir o que entrou na contagem.

**Requisitos** · Windows 10 versão 2004 (build 19041) ou posterior, em arquitetura x64. A estimativa ao vivo usa o OCR do Windows, os avisos de EXP visíveis e o jogo em janela sem bordas. A conferência por replay funciona mesmo quando o OCR está indisponível.

![Visão Geral da Bússola do Loot com oito partidas fictícias e distribuição de baús](docs/imagens/visao-geral.png)

*Exemplo com dados fictícios. O símbolo ≈ indica que o total pessoal inclui estimativas. Passe o mouse sobre o total para separar as aberturas atribuídas pelos replays das estimadas. O símbolo ≥ mostra o mínimo de baús do esquadrão que apareceu no seu replay. Aberturas distantes podem ficar fora da gravação.*

## Planejar uma rota

Abra **Mapa → Planejar rota**, escolha **Sugerir percurso de baús** e clique no ponto de pouso. Escolha um raio de 100, 180 ou 300 m. O planejador prioriza trechos que alcançam mais baús novos por metro. Arraste os pontos ou reordene as paradas pela lista, salve a rota e exporte a vista em PNG. O mapa da temporada Caçadores de Tesouro vem no aplicativo.

![Mapa de Crossbones Isle com um percurso de exemplo e o resumo de baús](docs/imagens/mapa-rota.png)

## Mostrar o contador na transmissão

No OBS ou Streamlabs, adicione uma **Fonte de navegador** com a URL `http://localhost:7777/` e mantenha a Bússola aberta no mesmo computador. Na aba **Estúdio do HUD**, compare os nove estilos pelas miniaturas. Alterne entre céu, neve, ilha e noite, simule a abertura de um baú e confira se o contador continua legível.

| Atalho no Windows | Ação |
| --- | --- |
| Ctrl + Shift + O | Mostrar ou ocultar o HUD no jogo |
| Ctrl + Shift + L | Travar ou liberar a posição do HUD |
| Ctrl + Shift + M | Alternar o estilo |

![Galeria de estilos e prévia do contador sobre o cenário de céu no Estúdio do HUD](docs/imagens/overlay.png)

## Dados e solução de problemas

| Situação | O que conferir |
| --- | --- |
| Minha partida ainda não apareceu | Aguarde o fechamento do replay e clique em **Sincronizar**. Os arquivos ficam em `%LOCALAPPDATA%\FortniteGame\Saved\Demos`. O filtro cobre partidas públicas dos modos indicados acima. |
| A estimativa ao vivo ficou em zero | Confira o idioma de OCR do Windows, o modo de janela e a visibilidade dos avisos de EXP. O replay consolida o resultado depois da partida. |
| Quero fazer backup | Com o aplicativo fechado, copie `%APPDATA%\ChestCounter`. Essa pasta guarda histórico, preferências e rotas. O CSV exportado pode incluir nomes dos colegas e IDs das partidas. |
| Quero conferir o download | No PowerShell, rode `Get-FileHash .\BussolaDoLoot-Setup-win-x64.exe -Algorithm SHA256` (ou no `.zip` portátil) e compare o valor com o arquivo `SHA256SUMS.txt` da mesma release. |

## Estrutura e compilação no Windows

```text
├── .github/workflows/          # CI e publicação automática de releases
├── docs/                       # Manual, guia rápido e imagens
├── experimental/rust-prototype # Protótipo experimental em Rust (não distribuído)
├── installer/                  # Script do instalador Windows (Inno Setup 6)
├── lib/                        # Leitor de replays incorporado e licenças
├── scripts/                    # Automação de build e empacotamento
├── src/                        # Aplicativo .NET 10 (GUI, Core, OCR e CLI)
└── tests/                      # Verificações automatizadas de contagem e rotas
```

Instale o **SDK .NET 10 x64** e o **Inno Setup 6** (para gerar o instalador `.exe`) e abra o PowerShell na raiz deste repositório.

```powershell
.\scripts\Publicar-Windows.ps1
```

O script executa os testes de contagem e rotas, compila `BussolaDoLoot.exe` e `chestcounter-live.exe` em executável único autossuficiente e grava em `artifacts\release\` o instalador **BussolaDoLoot-Setup-win-x64.exe**, o pacote **BussolaDoLoot-Portable-win-x64.zip** e o **SHA256SUMS.txt**. Para gerar apenas o pacote portátil sem o Inno Setup, use `.\scripts\Publicar-Windows.ps1 -SkipInstaller`. Para verificar contagem e rotas separadamente, execute `dotnet run --project tests/ChestCounter.Checks -c Release` e `dotnet run --project tests/ChestCounter.MapChecks -c Release`.

Consulte o passo a passo de exportação, as famílias de baús e as probabilidades no [manual do aplicativo](docs/MANUAL.md).

## Créditos

**master-yks** assina esta edição da Bússola do Loot. O leitor de replays é baseado no trabalho de **Shiqan** e nas adaptações de **LED-N**. A capa foi criada para esta edição com geração de imagem. As capturas mostram o aplicativo em modo de demonstração.

Projeto de fãs independente da Epic Games. O código próprio traz a licença MIT. A distribuição inclui OozSharp, com código de **Powzix** convertido por **SL-x-TnT** sob GPL versão 3 ou posterior. As artes e a cartografia pertencem aos respectivos titulares. [Licença do projeto](LICENSE) · [Origem e licenças dos componentes](lib/README.md) · [Fontes do mapa e das imagens](docs/MANUAL.md)
