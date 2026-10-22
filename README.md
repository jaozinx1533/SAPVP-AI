# SAPVP-AI

Um launcher open source em desenvolvimento para Minecraft Java. A versão atual é para Windows; Linux e macOS ainda não são suportados.

## O que já faz

- Restringe o seletor às versões 1.21.4, 1.21.8, 1.21.11 e 26.2.
- Autentica com conta Microsoft pelo fluxo da biblioteca CmlLib.
- Deixa escolher entre as versões 1.21.4, 1.21.8, 1.21.11 e 26.2. As pastas do jogo só são criadas ao iniciar uma versão, instalar um mod ou importar um modpack.
- Oculta a janela na área de ícones do Windows enquanto o Minecraft está aberto e a restaura quando o jogo fecha ou quando o jogador clica duas vezes no ícone do SAPVP-AI.
- A integração opcional de Rich Presence do Discord mostra a seção atual (Início, Biblioteca de mods ou Instâncias) e, separadamente, a sessão de Minecraft iniciada pelo launcher. Cada presença pode ser ativada ou desativada em Configurações.
- A biblioteca tem uma aba simples para pesquisar mods e outra para gerenciar instâncias com conteúdo. Pastas vazias deixadas por versões antigas do launcher não aparecem como instâncias.
- A página inicial mostra o estado da conta, versão escolhida, perfil de mods, quantidade de mods e novidades do projeto.
- Remover uma instância exige confirmação e envia a pasta completa para a Lixeira do Windows. Isso inclui jogo instalado, mods, saves do Minecraft, configurações e dados da versão.
- A aba **Mods** pesquisa o Modrinth para Fabric, instala mods e dependências na instância da versão selecionada e verifica atualizações dos mods instalados pelo launcher.
- Ao instalar um resultado, o launcher pede o destino entre instâncias salvas ou oferece uma versão permitida para criar a primeira instância. A busca de mods também tem um seletor de versão independente da versão escolhida para jogar.
- Cada versão pode ter perfis nomeados de conjuntos de mods. Criar um perfil salva uma cópia do conjunto atual; ativar outro perfil guarda o conjunto em uso e troca os arquivos JAR, sem misturar os mods entre perfis. Perfis podem ser renomeados ou excluídos; o perfil Padrão fica protegido.
- A inicialização não é bloqueada por alertas de compatibilidade do Modrinth. Os mods opcionais continuam sob escolha do jogador; a lista de mods proibidos para PvP continua bloqueada.
- Em **Configurações**, o botão de atualização consulta a Release pública mais recente do repositório GitHub configurado, valida o SHA-256 do instalador e abre o Setup do Windows. A primeira Release precisa ser publicada com a tag `v1.0.0` ou superior.
- O workflow em `.github/workflows/release.yml` prepara o executável e o instalador x64 quando uma tag estável como `v1.0.1` é enviada ao GitHub. O script Inno Setup instala sem exigir acesso de administrador e cria atalhos.
- A verificação de atualizações lista as novas versões compatíveis com Minecraft e Fabric antes de pedir confirmação para baixar.
- É possível importar modpacks `.mrpack` do Modrinth Fabric. O launcher valida hashes dos downloads e extrai as configurações de cliente do pacote.
- A busca de mods tem seleção de versão, ordenação e instalação direta; ao instalar, escolha uma instância existente ou crie uma para a versão do mod.
- Os resultados do Modrinth têm página de detalhes, filtros por categoria (incluindo desempenho), seleção da publicação mais recente compatível com as versões permitidas e ações para atualizar/remover mods na instância. A atualização automática fica habilitada para mods instalados pelo launcher; arquivos externos podem ser removidos, mas não atualizados pelo catálogo.
- O jogo inicia com Fabric automaticamente quando encontra mods na instância; versões sem mods continuam iniciando normalmente.
- Em **Configurações**, escolha o Java manualmente ou deixe a seleção automática, ajuste memória inicial/máxima, aplique uma sugestão de RAM, inicie em tela cheia e configure argumentos Java avançados.
- A aba **Novidades** recebe mensagens do canal Discord pelo relay Cloudflare em `DiscordMinecraftNewsBot`. O feed mantém publicações por 7 dias; apagar a mensagem original no Discord remove a novidade para todos. Cada jogador pode marcar uma novidade como lida; ela fica visível por mais 4 horas e então é ocultada apenas naquele computador.

Os dados ficam em `%AppData%\\MinecraftProjectLauncher\\instances\\<versão>`. O launcher não prepara as pastas das versões antes de elas serem usadas. O jogo continua podendo criar e usar seus próprios saves; eles não aparecem como seção na interface e permanecem compartilhados entre perfis da mesma versão. Cada perfil guarda snapshots de mods, configurações, resourcepacks, shaderpacks e opções de vídeo.

A conta Microsoft precisa ter uma licença válida de Minecraft Java. O launcher não oferece modo offline para contornar autenticação ou licença.

## Direção futura

A integração da IA de treino será adicionada quando a IA do projeto estiver pronta. A ideia é apresentar perguntas sobre a rotina depois que o jogo for fechado.

O bot Discord roda no computador do administrador e publica no Cloudflare Worker. Os jogadores têm acesso somente de leitura; apenas o bot possui o segredo de publicação e exclusão. As instruções de deploy e configuração estão em `DiscordMinecraftNewsBot/LEIA-ME.txt`.

O endereço do feed não é editável na interface do launcher. Ele está fixado em `Services/NewsFeedConfiguration.cs` para esta distribuição. O feed precisa ser acessível em leitura para os jogadores receberem as novidades; a publicação e exclusão exigem o segredo privado do bot.

As atualizações do launcher usam o endpoint público de Releases do repositório `jaozinx1533/SAPVP-AI`. Os arquivos do launcher e a workflow de Release precisam estar na raiz desse repositório para a publicação automática funcionar.

## Desenvolver

- Windows 10 ou posterior
- .NET 8 SDK ou SDK mais recente compatível
- Visual Studio com a carga de trabalho de desenvolvimento para desktop .NET, ou outro editor compatível

Abra `MinecraftProjectLauncher.csproj` no Visual Studio e execute o projeto. Para iniciar pelo terminal, use `dotnet run --project MinecraftProjectLauncher.csproj` nesta pasta.

## Dependências

- [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core) (MIT): instalação e inicialização do Minecraft Java.
- [CmlLib.Core.Auth.Microsoft](https://github.com/CmlLib/CmlLib.Core.Auth.Microsoft) (MIT): login Microsoft, Xbox e Minecraft Java.

## Plataformas

A interface atual usa WPF e a integração com a bandeja usa a API do Windows, portanto esta versão funciona somente no Windows. O suporte a Linux e macOS exige migrar a interface para uma tecnologia multiplataforma e trocar os recursos específicos do Windows (bandeja, Lixeira e instalador). A biblioteca CmlLib usada para autenticação e inicialização do Minecraft declara suporte a Windows, Linux e macOS; isso não torna a interface WPF multiplataforma.

## Licença deste projeto

Este projeto está licenciado sob a licença MIT. Consulte o arquivo `LICENSE`.
