# Desenvolvimento e promoção de melhorias

Para desenvolvimento, execute `dotnet run --project src/ChestCounter.Gui -c Testes` na raiz desta cópia com o SDK .NET 10. A configuração de compilação `Testes` identifica a janela, usa `%APPDATA%\ChestCounter-Testes` para histórico, rotas e preferências e atende o OBS em `http://localhost:7778/`. Os botões da janela controlam o HUD de testes. Os atalhos globais ficam reservados à edição estável. Os replays do Fortnite servem de entrada para ambas.

A pasta `Testes` usa a branch `testes`. A pasta oficial `Chest Tracker\Repositorio` usa `main`. São worktrees do mesmo repositório. Para localizar ambas, execute `git worktree list`.

## Validar uma mudança

Registre cada melhoria em um commit próprio na branch de testes. Execute na raiz dela:

```powershell
dotnet run --project tests/ChestCounter.Checks -c Testes
dotnet run --project src/ChestCounter.Gui -c Testes -- --verify-ui artifacts/verificacao
```

Abra o aplicativo de testes para conferir o fluxo alterado. Para incorporar novidades da versão oficial à branch de testes, use `git fetch origin` e `git merge origin/main` na pasta de testes com o trabalho salvo.

## Promover apenas o que foi aprovado

Na pasta oficial, crie uma branch a partir de uma `main` atualizada. Aplique os commits aprovados com `git cherry-pick <hash>` na ordem em que foram criados, incluindo eventuais dependências. Confira `git diff main...HEAD` e execute `scripts\Publicar-Windows.ps1` para validar os testes e gerar o instalador único em `artifacts\release\`. Publique a branch de revisão e abra um pull request para `main` quando quiser integrar a atualização (ao criar uma tag `v*`, o workflow `.github/workflows/release.yml` também publica a release automaticamente). A edição Release usa os dados e a porta habituais.
