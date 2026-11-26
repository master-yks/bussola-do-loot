# Bússola do Loot

Tracker de baús do Fortnite para Windows · **master-yks**

Aplicativo de Windows para acompanhar seus baús e os baús visíveis dos colegas em partidas públicas de Battle Royale e Construção Zero (Solo, Dupla, Trio e Esquadrão).

## Como usar

1. Execute o instalador `BussolaDoLoot-Setup-win-x64.exe` (recomendado) ou extraia `BussolaDoLoot-Portable-win-x64.zip` e abra `BussolaDoLoot.exe`. Ambos os formatos já incluem o runtime .NET em executável único. Na versão portátil, `Criar atalhos.cmd` cria o atalho na Área de Trabalho.
2. Ative a gravação de replays no Fortnite. O painel sincroniza os arquivos em `%LOCALAPPDATA%\FortniteGame\Saved\Demos`.
3. Para ver uma estimativa pessoal durante a partida, deixe os avisos de EXP visíveis, use janela sem bordas e habilite o OCR de um idioma compatível no Windows. A conferência dos colegas acontece ao finalizar o replay.
4. Em **Visão Geral → Seus baús por tipo**, escolha 24 horas, 7 dias, temporada ou todo o histórico. Cada cartão mostra quantas aberturas pessoais o replay atribuiu àquela família.
5. No Histórico, passe o mouse sobre a partida para ver a contagem por colega e os baús visíveis sem autor confirmado. O CSV também inclui esses campos. O histórico local fica em `%APPDATA%\ChestCounter`.

## Mapa e rotas

A aba **Mapa** abre Crossbones Isle em **Explorar**. A visão geral agrupa os objetos pela região nomeada mais próxima. Clique em uma contagem para ampliar e passe o mouse sobre um grupo para ver sua composição. Um clique em um objeto individual mostra seu tipo e sua região. As 16 camadas têm ícones próprios e busca por nome. **Só baús**, **Todas** e **Limpar** trocam a seleção em um clique.

Ao ampliar, o minimapa de 2048 × 2048 dá lugar ao terreno ortográfico da versão 43.10. A área principal da ilha usa recortes com resolução equivalente a 8192 × 8192 no quadro completo, armazenados no aplicativo. A roda amplia em torno do cursor. O botão ao lado de **Localizar região** recolhe ou abre o painel. [Minimapa e nomes das regiões](https://fortnite-api.com/v1/map?language=pt-BR) · [Terreno ortográfico da Fortiq](https://fortiq.app/fortnite-43-10-map/) · [Pontos de saque](https://lifecrisis.life/loot) · [Objetos da ilha e escala](https://fortnite.gs/map/)

Em **Planejar rota**, escolha **Sugerir percurso de baús** e clique no pouso. Escolha 100, 180 ou 300 m para buscar até 4, 8 ou 12 destinos além do pouso. A cada trecho, o planejador considera os baús das camadas ativas dentro do alcance de coleta e prioriza o ganho de novos baús pela distância. O raio de busca parte do pouso ou da última parada. Cada sugestão limita a caminhada ao dobro desse raio (200, 360 ou 600 m). As camadas de baús ativas são salvas junto com a rota. **Continuar a partir do fim** acrescenta um novo trecho. Paredes, andares e relevo podem exigir desvios. Use a lista **Paradas do percurso** para centralizar uma parada e ver a distância desde a anterior. As setas da lista antecipam ou adiam uma parada. Com o mapa em foco, as setas do teclado deslocam a vista, + e − ajustam o zoom e Home volta à ilha.

Para traçar à mão, clique para adicionar paradas, arraste para mover, use **Shift + clique** para inserir uma entre duas existentes e o botão direito para remover. **Desfazer** reverte a última edição ou sugestão inteira. O alcance de coleta conta cada registro de baú próximo do traçado uma vez, somente nas camadas ativas. A distância é horizontal e a disponibilidade varia por partida.

Dê um nome ao trajeto e escolha **Salvar**. Salvar com o mesmo nome atualiza a rota em `%APPDATA%\ChestCounter\rotas`. **Enquadrar** mostra o trajeto inteiro. **Exportar vista em PNG** grava o enquadramento atual com distância e contagem de baús. Mapa, ícones e planejamento funcionam offline.

## Baús da minitemporada Caçadores de Tesouro

| Família | Como ler o contador |
| --- | --- |
| Comum | Baú dourado da minitemporada, com caveira, correntes e gemas. Inclui o surgimento garantido |
| Raro | Baú raro, representado pela arte prateada e azul de Caçadores de Tesouro |
| Enterrado | Baú de tesouro desenterrado. O mapa com um X indica a escavação |
| Elemental | Baú de Elemental (Sprite) identificado pela classe do recipiente. As artes de Fortuna e Amaldiçoado aparecem abaixo dos cartões |
| Do cartaz | Baú cujo identificador indica um cartaz ou um Elemental enterrado. A miniatura do cartaz sobre o baú indica a origem |
| Sem tipo | Aberturas pessoais com classe desconhecida, estimativas do OCR e partidas antigas sem replay disponível |

As aberturas da partida aparecem no painel ao vivo. A identificação da família acontece na sincronização do replay. Ao atualizar de uma versão anterior, o aplicativo relê os replays disponíveis e conserva o histórico em `%APPDATA%\ChestCounter`. O histórico mostra a distribuição de cada partida e a busca aceita nomes de tipos. A exportação CSV inclui uma coluna por família.

## Como interpretar o squad

O símbolo **≥** indica o mínimo visto no seu replay. Um colega pode abrir baús longe de você sem que a gravação receba o evento. O contador usa o vínculo do personagem com o jogador no momento da abertura e a equipe registrada na partida. Uma caixa já aberta pode reaparecer em vários pacotes e canais. Ela só conta outra vez quando o replay registra que voltou a ficar fechada.

Baús destruídos entram para um jogador quando a gravação identifica o responsável pela interação. Eventos sem autor resolvido ou com identidades conflitantes aparecem à parte. Esse número pode incluir adversários. Registros antigos sem replay disponível conservam os valores da versão que os calculou. O campo sem autor fica vazio para esses registros e para estimativas por OCR.

## Estrutura e distribuição

| Pasta | Conteúdo |
| --- | --- |
| `src/` | Aplicativo distribuído, CLI, OCR e núcleo de contagem em .NET 10 |
| `lib/` | Leitor de replay e licenças das dependências incorporadas |
| `installer/` | Script do instalador Windows (Inno Setup 6) |
| `tests/` | Verificações de contagem, histórico, reprocessamento e rotas |
| `scripts/` | Geração do instalador e do pacote portátil de Windows |
| `artifacts/release/` | Distribuições geradas localmente (`.exe`, `.zip` e `SHA256SUMS.txt`) |
| `experimental/rust-prototype/` | Protótipo experimental em Rust com armazenamento próprio em `%APPDATA%\ChestCounter-Rust` |

Para compilar e gerar o instalador e o pacote portátil, use o SDK .NET 10 e o Inno Setup 6 no Windows. A partir da raiz do projeto, execute:

```powershell
.\scripts\Publicar-Windows.ps1
```

O script executa os testes, publica `BussolaDoLoot.exe` e `chestcounter-live.exe` em executável único autossuficiente e grava em `artifacts/release/` o instalador `BussolaDoLoot-Setup-win-x64.exe`, o portátil `BussolaDoLoot-Portable-win-x64.zip` e o `SHA256SUMS.txt`. Para conferir a contagem diretamente no código:

```powershell
dotnet run --project tests/ChestCounter.Checks -c Release
dotnet run --project tests/ChestCounter.Checks -c Release -- "$env:LOCALAPPDATA\FortniteGame\Saved\Demos"
```

O segundo comando analisa os replays em memória, repete a sincronização e compara a leitura individual de até três partidas em grupo com a leitura em paralelo. O aplicativo também inclui o comando `chestcounter-live.exe --scan` para atualizar o histórico local.

## Créditos das imagens

O token do Windows usa a vista frontal do render de 1.000 px fornecido com o modelo 3D. As artes e a cartografia pertencem aos respectivos titulares. Este aplicativo de fãs é independente da Epic Games. O código próprio traz a licença MIT. O descompressor OozSharp incorpora código de Powzix sob GPL versão 3 ou posterior, convertido para C# por SL-x-TnT. Os textos das licenças acompanham o código e o pacote para Windows.

| Imagem | Fonte |
| --- | --- |
| Tesouro dourado e raro prateado | [Modelos Treasure Hunters no Fortnite Wiki](https://fortnite.weirdgloop.org/w/Chest) |
| Elemental comum, Fortuna, Amaldiçoado e miniatura do cartaz | [Galeria de recipientes do Fortnite Wiki](https://fortnite.weirdgloop.org/w/Containers) |

Os cinco modelos de baú usam os PNGs originais de 512 × 512 px. Os cartões Enterrado e Do cartaz reutilizam a arte do recipiente com um marcador de origem. As capturas da [rota de escavação](https://fortniteshoptoken.wiki/chest-routes/the-chest/) e da [caça por cartaz](https://fortniteshoptoken.wiki/chest-guides/sprite-wanted-posters/) mostram essas duas interações em jogo.

## Funcionalidades Principais

- **Visão Geral Completa**: Painel duplo com **Total Acumulado na Temporada** + **Nesta Partida (Ao Vivo)**, evolução partida a partida com linha de média, ranking de parceiros de esquadrão, tesouros encontrados e cronômetro ao vivo da **Hora do Tesouro**.
- **Gráficos Interativos de Probabilidade**:
  - Curva teórica exata $P(X \ge 1) = 1 - (1 - 1/k)^N$ com comparativo da **Hora do Tesouro**, **chance compartilhada com esquadrão** (Ficha da Loja Pirata `1/137.500` e Engenhocas `1/127`), marcador luminoso **"VOCÊ"** e simulação interativa ao passar o mouse;
  - Gráfico real da **Evolução da Sua Chance na Temporada** partida a partida;
  - Matriz completa das 7 categorias de recompensas com projeções para **+50 / +100 partidas** no seu ritmo real.
- **Histórico Meticuloso & Exportação CSV**: Tabela auditável de todas as partidas contabilizadas (data/hora, modo, seus baús, esquadrão e tesouros) com exportação em 1 clique para `.csv`.
- **Sobreposição no Jogo (`Ctrl+Shift+O` / `Ctrl+Shift+L`) & OBS (`http://localhost:7777/`)**: HUD transparente sobre o Fortnite e servidor local para Fonte Navegador no OBS/Streamlabs.
