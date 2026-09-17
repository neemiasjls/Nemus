# Nemus

Finanças pessoais com razão de partidas dobradas, orçamento por envelope e
tratamento de parcelamento de cartão brasileiro.

**Estado: fase 8 — gastos fixos.** O razão está pronto e provado por teste;
sobre ele há uma API REST em ASP.NET Core e uma interface React onde dá para
criar conta, criar categoria, lançar despesa, importar extrato OFX do banco,
**dividir o dinheiro do mês entre envelopes** (com sobra e estouro rolando
para o mês seguinte), registrar **compra parcelada de cartão**, ver
**quanto cada categoria consome mês a mês** e manter a lista dos
**gastos fixos**, conferida contra o que o razão registrou.

Referências conceituais: o *método* de orçamento base zero por envelope (YNAB)
e a *arquitetura* de partidas dobradas com API própria (Firefly III). Nenhum
código, layout, texto ou marca de qualquer um dos dois foi copiado — só
conceito e documentação pública.

---

## Os quatro pilares e onde cada um vive

| Pilar | Onde é garantido |
|---|---|
| 1. Partidas dobradas | `Transaction.Create` (impossível construir desbalanceada) + `trg_entries_balanced` / `trg_transactions_balanced`, gatilhos diferidos |
| 2. Dinheiro inteiro | `Money` sobre `long`, sem nenhum `decimal`/`double`/`float` na API; `BIGINT` no banco; teste de arquitetura varre o assembly por reflexão |
| 3. Importação idempotente | `ux_transactions_idempotency` sobre `(source, source_account_ref, external_id)` + `TransactionRepository.ImportAsync` + `OfxImporter` |
| 4. Orçamento com rollover | `budget_assignments` (única coisa guardada) + as visões `v_budget_entries`, `v_budget_months` e `v_budget_ready_to_assign`; `v_budget_integrity` prova a invariante, como `v_ledger_integrity` faz com o razão |

---

## Decisões de modelagem

Quatro pontos tinham alternativa razoável. O que ficou:

**Escala monetária: centavos (2 casas), não milliunits (3).** Milliunits cria
uma classe de valor representável mas impagável — `33333` milliunits é
R$ 33,333, que não existe como pagamento no Brasil. Toda entrada (OFX, Pluggy,
boleto, fatura) tem 2 casas e toda saída também. A escala vive em
`currencies.decimal_places`, não numa constante: migrar para milliunits é
`UPDATE` mais reescala dos dados, não migration de schema.

**N pernas somando zero, não origem/destino fixos.** Duas colunas
`from`/`to` são mais simples e morrem na fase 2 (compra dividida: um
supermercado, duas categorias, uma linha de extrato) e na fase 6
(parcelamento). O caso de duas pernas segue sendo o comum; deixou de ser o
único possível. Custo: toda leitura vira um join.

**Favorecido é tabela própria, não conta externa por estabelecimento.** O
Firefly faz cada payee virar uma conta de despesa; isso explode o cadastro de
contas e transforma "juntar dois cadastros do mesmo mercado" numa fusão de
contas do razão. Como dimensão, fundir é um `UPDATE`. A consulta por
estabelecimento sai de um `GROUP BY payee_id`.

**Migrations em SQL puro, com runner próprio.** `CONSTRAINT TRIGGER` diferido,
índice único parcial e FK composta não são modeláveis em EF Core Migrations —
e são exatamente as peças que sustentam as invariantes. O acesso a dados é
Npgsql direto, sem ORM.

A contraparte de uma despesa é uma **conta de verdade** (tipo `EXPENSE` ou
`REVENUE`), não um buraco por onde o dinheiro sai do sistema. É isso que torna
a soma de **todos** os saldos exatamente zero, sempre — a propriedade que o
teste do razão verifica. Patrimônio líquido é a soma das contas internas.

---

## Como rodar

Não há aplicação para subir. A fase 1 é schema, biblioteca de domínio e
testes — nenhum processo fica no ar, nada abre no navegador. Isso chega na
fase 2.

O que `start.ps1` faz é deixar o ambiente de trabalho pronto: sobe o serviço
do PostgreSQL local, cria os bancos se faltarem, aplica as migrations no banco
de desenvolvimento e roda a suíte completa.

```bash
.\start.ps1
```

```bash
.\stop.ps1
```

Pede elevação (UAC) só para mexer no serviço, e pede a senha do Postgres na
hora — ou lê de `NEMUS_PG_PASSWORD`, se você preferir não digitar sempre. A
senha nunca é gravada em disco.

Opções: `-PgVersion 18` (padrão 16), `-SkipTests`, `-SkipMigrations`,
`-Port`, `-DevDatabase`, `-TestDatabase`. No `stop.ps1`, `-All` para as duas
instalações.

Terminado o `start.ps1`, o que existe para olhar é o schema no banco `nemus`
— abra num cliente e rode `SELECT * FROM v_ledger_integrity;`. Num razão
íntegro, toda coluna é zero.

## Rodar a API e a interface localmente

Com o banco de pé (`start.ps1`), em dois terminais:

```bash
$env:NEMUS_DB="Host=localhost;Port=5433;Database=nemus;Username=postgres;Password=SUA_SENHA"; $env:NEMUS_BOOTSTRAP_TOKEN="qualquer-coisa-com-32-caracteres-ou-mais"; $env:NEMUS_CORS_ORIGINS="http://localhost:5173"; dotnet run --project src/Nemus.Api
```

```bash
cd web; echo "VITE_NEMUS_API=http://localhost:5000" > .env.local; npm install; npm run dev
```

A interface pede o token na entrada. Ele fica só em `sessionStorage` — some
quando a aba fecha, de propósito: é credencial de acesso total ao razão.

`npm test`, dentro de `web`, roda a verificação de `money.ts` — ida e volta
de formatação e leitura, incluindo o `0,10 + 0,20` que quebra em ponto
flutuante. Sem framework de teste: é `node --experimental-strip-types` sobre
o próprio arquivo.

### Só olhar a interface, sem banco nem API

```bash
cd web; npm install; npm run demo
```

Sobe uma API falsa com seis meses de lançamentos e o frontend apontado para
ela, em http://localhost:5173. Qualquer token entra; escritas são aceitas e
descartadas.

Não são números soltos: é um razão em miniatura. Toda transação tem pernas
que somam zero, os saldos saem delas, e o script **se recusa a subir** se a
soma de todos os saldos não der zero. As datas são relativas a hoje, então o
painel sempre tem dados no mês corrente. Nada disso vai para produção — o app
não importa o script e o build não o inclui.

## Rodar os testes na mão

Sem nenhuma configuração, os testes de domínio rodam e os 18 de banco pulam
com mensagem explícita:

```bash
dotnet test
```

Para rodar também os de banco, aponte `NEMUS_TEST_DB` para um PostgreSQL
**local**. Crie o banco uma vez (o nome precisa conter `test`):

```bash
createdb -h localhost -p 5433 -U postgres nemus_test
```

E rode a suite completa (PowerShell):

```bash
$env:NEMUS_TEST_DB="Host=localhost;Port=5433;Username=postgres;Password=SUA_SENHA;Database=nemus_test"; dotnet test
```

Duas barreiras independentes protegem contra acidente, porque a suite começa
com `DROP SCHEMA public CASCADE`:

- **host precisa ser local** (`localhost`, `127.0.0.1`, `::1`). Nenhum host
  remoto é aceito, em hipótese alguma.
- **nome do banco precisa conter `test`**.

Nome de banco sozinho não bastaria: a maioria dos Postgres gerenciados chama
o banco de `postgres`, e um projeto batizado de `nemus-test` passaria no
filtro sem problema nenhum. Por isso as duas.

## Aplicar migrations em outro banco

```bash
$env:NEMUS_DB="Host=localhost;Port=5433;Database=nemus;Username=postgres;Password=SUA_SENHA"; dotnet run --project src/Nemus.MigrationTool -- apply
```

`status` lista o que está aplicado e o que falta sem escrever nada. O runner
guarda o SHA-256 de cada migration aplicada: editar uma que já rodou faz a
próxima execução acusar, em vez de deixar dois bancos divergirem em silêncio.

---

## Estrutura

```
db/migrations/          SQL puro, versionado, também embutido no assembly
  001 moedas e tipos de conta
  002 contas e condições de cartão
  003 categorias, favorecidos, normalização de descrição
  004 razão: lotes de importação, transações, lançamentos
  005 invariantes: gatilhos diferidos de balanceamento
  006 parcelamento (estrutura da fase 6)
  007 visões de leitura e integridade
  008 controle de acesso: RLS, security_invoker, search_path
  009 correção: patrimônio líquido não soma EQUITY
  010 orçamento: atribuições e as visões de envelope
  011 acesso: usuário com senha em hash, e sessão revogável

src/Nemus.Domain/       Sem dependência nenhuma. Nenhum ORM, nenhum driver.
  Monetary/             Money, Currency
  Ledger/               Transaction (raiz de agregado), Entry
  Accounts/             Account, AccountType, SystemAccounts
  Categories/ Payees/   dimensões
  Budgeting/            BudgetMonth, BudgetAssignment
  Identity/             User (regra de senha), Session
  Installments/         estrutura da fase 6
  Primitives/           Result, Error, UuidV7

src/Nemus.Infrastructure/
  Migrations/           runner próprio com verificação de hash
  Persistence/          repositórios sobre Npgsql
  Import/               leitor de OFX 1.x (SGML) e 2.x (XML)
  Security/             hash de senha (PBKDF2) e token de sessão

src/Nemus.MigrationTool/
                        CLI: migrations, e `user add` / `user password`

src/Nemus.Api/          ASP.NET Core minimal API
  Security/             porteiro de sessão, freio de login, cabeçalhos
  Contracts/            DTOs próprios da borda HTTP (nunca os do domínio)
  Endpoints/            acesso, contas, categorias, lançamentos, orçamento

web/                    React + TypeScript, build por Vite
  src/App.tsx           casca: navegação lateral, rotas por hash, carregamento
  src/pages/            entrada, visão geral, orçamento, lançamentos, categorias,
                        parcelas, contas, importar, trocar senha
  src/components/       painel, tabela com razão em T, indicadores, tema
  src/charts/           colunas, barras e rosca em SVG escrito à mão
  src/lib/money.ts      dinheiro inteiro no frontend; sem float em lugar nenhum
  src/lib/analysis.ts   números do painel, lidos nas contas externas
  src/lib/budget.ts     arruma os envelopes para a tela; não calcula dinheiro
  src/lib/theme.ts      claro, escuro ou sistema
  src/lib/api.ts        cliente da API

tests/Nemus.Tests/
  Domain/               domínio e segurança; property-based com seed reproduzível
  Database/             contra Postgres real, pulam sem NEMUS_TEST_DB
```

---

## Convenção de nomes

Identificadores de código em **inglês** — arquivos, tipos, funções,
variáveis, classes e variáveis de CSS. Comentários, textos da interface e
mensagens de erro em **português**.

A exceção deliberada são os nomes de teste, que leem como especificação em
português (`Soma_de_todos_os_saldos_permanece_zero`). Essa convenção vem da
fase 1. Rotas da interface também ficam em português (`#/lancamentos`),
porque aparecem na barra de endereço; o código trabalha com o id em inglês.

---

## A interface

O conceito é o **livro‑razão**, não o painel de fintech de sempre: serifa nos
números, algarismo tabular, verde de crédito e oxblood de débito, fio duplo
sob o título, e as partidas mostradas como **razão em T** — saiu à esquerda,
entrou à direita, e os dois lados sempre fecham no mesmo total.

Seis seções com navegação lateral: visão geral, **orçamento**, lançamentos,
categorias, contas e importação — nessa ordem, que é a do uso: o produto é
organizar gasto, não acompanhar patrimônio. A visão geral abre pelo gasto do
mês, pelos envelopes e pelo que falta atribuir; patrimônio líquido é o último
cartão. Formulários ficam recolhidos até serem pedidos.

**Temas.** Claro, escuro ou seguindo o sistema, com o seletor no rodapé da
barra lateral. A escolha é aplicada por `public/theme.js` *antes* da primeira
pintura — arquivo próprio, e não script inline, porque a CSP bloqueia script
inline. Sem isso, quem escolheu escuro num sistema claro veria um lampejo
branco a cada carregamento.

**Sem biblioteca de gráfico, de ícone ou de fonte.** A CSP só aceita script
do próprio domínio e não tem `font-src`. Gráficos e ícones são SVG escrito à
mão, e toda geometria dinâmica é atributo SVG — nenhum `style=""` no JSX.

**Números do painel** são lidos nas contas externas: gasto é o que entrou em
"Despesas externas", receita é o que saiu de "Receitas externas". Transferência
entre contas próprias não aparece como gasto, e estorno se desconta sozinho.
O cálculo enxerga os 200 lançamentos mais recentes (teto de página da API); a
tela avisa quando há mais, e agregar no servidor é trabalho da fase 7.

---

## O orçamento — o que é guardado e o que é calculado

A invariante do orçamento, irmã da soma zero do razão:

> **Σ(disponível de todos os envelopes) + pronto para atribuir = saldo das
> contas do orçamento**

Todo real que existe nas suas contas está dentro de um envelope ou esperando
para entrar em um. Não há terceiro lugar. A tela mostra essa conta o tempo
todo, com o selo de conferido — do mesmo jeito que o rodapé mostra Σ saldos = 0.

**Só a atribuição é guardada.** A tabela `budget_assignments` tem uma linha
por categoria e mês. Atividade e disponível são calculados do razão, em visão.

| Decisão | Alternativa | Por que assim |
|---|---|---|
| Calcular disponível | Materializar `(categoria, mês, atribuído, atividade, disponível)` | Cache que **diverge em silêncio**: edite uma transação de três meses atrás e os meses seguintes ficam sutilmente errados, sem erro nenhum aparecer. Há um teste exatamente disso |
| Estouro rola negativo | Zerar a categoria e cobrar no total | Foi o que você especificou — e é também o que deixa `disponível` virar uma **soma acumulada** (uma função de janela), em vez de uma recursão mês a mês |
| Pronto para atribuir vem das **entradas** | `saldo − Σ disponível` | Assim `v_budget_integrity` compara três números vindos de caminhos diferentes. Definido como "saldo menos envelopes", a verificação seria verdadeira por definição e não verificaria nada |
| Só categoria de despesa recebe atribuição | Validar no código | FK composta `(category_id, category_kind)` → `categories (id, kind)`: nem SQL direto passa. Receita não entra em envelope, ela vira dinheiro a atribuir |

**As três regras do que o orçamento enxerga do razão** (`v_budget_entries`):

1. **Só transação que toca conta do orçamento.** Despesa paga direto de um
   investimento marcado como fora do orçamento não consome envelope — aquele
   dinheiro nunca esteve no orçamento.
2. **A categoria conta na perna da conta externa**, onde a interface e a
   importação a colocam. Ali o sinal já é o do orçamento: gasto de R$ 45,90 é
   `+4590` em "Despesas externas". Estorno entra negativo e recompõe o
   envelope sozinho; reembolso categorizado na própria categoria de despesa
   também.
3. **Perna na conta de despesa sem categoria de despesa** é dinheiro que saiu
   do orçamento sem envelope. Não some: reduz o pronto para atribuir e a tela
   conta quantos lançamentos estão assim. Dinheiro sem categoria tem que
   incomodar, senão o orçamento vira decoração.

**O extrato importado entra no orçamento pela categorização.** O OFX do banco
não traz categoria, então todo gasto importado nasce fora de envelope — e
aparece como "saiu sem categoria", reduzindo o pronto para atribuir. A tela de
orçamento conta quantos são e leva direto à lista já filtrada (`Categorizar`),
onde a categoria se muda na própria linha. Categoria é dimensão, não dinheiro:
categorizar move o valor do "sem envelope" para o envelope e **não mexe no
saldo de conta nenhuma**.

A categoria vai na perna da conta externa, que é onde o orçamento a procura.
Por isso o lançamento precisa ter exatamente uma: transferência entre contas
suas não tem nenhuma (o dinheiro não saiu do seu patrimônio) e lançamento
dividido tem várias — escolher uma só apagaria a divisão que alguém fez de
propósito. Nos dois casos a API recusa com a explicação, em vez de adivinhar.

**Cartão dentro do orçamento** funciona sem tratamento especial: a compra sai
do envelope na hora, e pagar a fatura é dinheiro trocando de conta dentro do
orçamento — não mexe em envelope nenhum. O que o Nemus ainda não faz é
*separar* o dinheiro da fatura num envelope de pagamento, como o YNAB faz;
isso encosta na fase 6.

---

## Como a API se protege

A API expõe o razão na internet, então as decisões de segurança são parte do
desenho, não um acabamento:

| O quê | Como |
|---|---|
| Autenticação | Usuário e senha; o login devolve um **token de sessão** que vai em `Authorization: Bearer`. No banco fica só o SHA-256 dele — vazou o banco, ninguém ganha sessão usável |
| Senha | **PBKDF2-SHA256, 210 mil iterações**, sal por usuário, formato auto-descritivo (`algoritmo$iterações$sal$hash`). Custo antigo é regravado sozinho no login seguinte |
| Enumeração de usuário | O login confere a senha contra um hash de mentira quando o usuário não existe: mesma resposta, **mesmo tempo**. E a mensagem é uma só para usuário errado e senha errada |
| Força bruta na senha | Freio por usuário e origem: 8 erros em 15 minutos e trava, com `Retry-After`. Acertar zera na hora |
| Revogação | Sessão vive em tabela, não em JWT. “Sair” e trocar a senha derrubam na hora — JWT só cairia ao expirar |
| Falha ruidosa | Sem `NEMUS_BOOTSTRAP_TOKEN`, ou com token curto, **o processo não sobe**. Serviço que não sobe é incidente visível; serviço aberto é incidente invisível |
| Mass assignment | DTOs próprios da borda. `Id`, `CreatedAt`, `ImportBatchId` e `External` **não existem** no contrato de entrada — o cliente não pode informar o que não tem campo |
| Limite de requisições | 60 por minuto por IP, para proteger o servidor. A senha tem freio próprio, bem mais apertado |
| CORS | Só as origens em `NEMUS_CORS_ORIGINS`. Sem a variável, **nenhuma** origem é liberada |
| Erros | Código estável e frase legível. Nunca stack trace, nome de tabela ou SQL |
| Dinheiro na rede | Unidades mínimas, **inteiro**. JSON não distingue inteiro de real e todo parser de JS lê `1234.56` como float — aceitar decimal furaria o pilar 2 justo na fronteira |

### Entrar

O `NEMUS_BOOTSTRAP_TOKEN` **não abre mais o razão**. Ele autoriza uma coisa só:
criar o primeiro acesso, enquanto não existe nenhum usuário. Sem isso, quem
achasse a URL da API recém-subida antes de você viraria o dono do seu razão;
depois que o primeiro usuário existe, aquele endpoint responde 409 para sempre.

1. Suba a API com `NEMUS_BOOTSTRAP_TOKEN` definido.
2. Abra o site. Como ainda não há usuário, a tela vira **primeiro acesso**:
   escolha o usuário, a senha, e cole o token de instalação.
3. A partir daí é usuário e senha. A sessão vale 30 dias no navegador.

**Esqueceu a senha?** Não há e-mail de recuperação — seria mais superfície para
errar num app de uma pessoa só. Quem tem acesso ao banco resolve pela linha de
comando, e a senha é digitada na hora, sem aparecer em argumento nem no
histórico do shell:

```bash
dotnet run --project src/Nemus.MigrationTool -- user password SEU_USUARIO
```

O mesmo comando com `user add` cria outro usuário, e `user list` só conta
quantos existem.

---

## Onde isto roda

Três fornecedores, um para cada camada:

| Camada | Onde | Estado |
|---|---|---|
| Banco | Supabase `sa-east-1` (São Paulo) | **no ar**, 9 migrations aplicadas; a 010 (orçamento) ainda não foi |
| Frontend | Cloudflare | **no ar** — https://nemus.neemiasdjlsilva.workers.dev |
| API .NET | Render (Docker) | pronta para subir; precisa de um remoto Git |

O frontend já está publicado mas **ainda não funciona de ponta a ponta**:
ele aponta para `https://nemus-api.onrender.com`, que só existe depois que a
API subir. Até lá a tela de entrada responde que não conseguiu falar com a
API — comportamento correto, não defeito.

A divisão em três não é gosto: **Cloudflare Workers não roda .NET** (isolates
V8, não CLR), então a API precisa de um host com runtime .NET ou contêiner —
e o Render faz isso pelo `Dockerfile` na raiz.

**Banco: Supabase**, projeto `Nemus`, região `sa-east-1` (São Paulo).
As 8 migrations estão aplicadas e o razão foi verificado lá — transação
desbalanceada rejeitada com `NM001`, perna solta com `NM002`, conta de
sistema protegida com `NM004`.

Conexão para a API .NET (a senha fica no painel do Supabase, nunca no
repositório):

```
Host=db.SEU-PROJETO.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=...;SSL Mode=VerifyFull
```

Use a conexão **direta** (5432) ou o pooler em modo sessão. O pooler em modo
transação (6543) não aguenta o DDL destas migrations.

Cloudflare D1 não substitui o Postgres aqui: é SQLite, e não tem
`CONSTRAINT TRIGGER` diferido, índice único parcial nem coluna gerada — que
são justamente as peças que sustentam as invariantes.

### Subir a API no Render

O Render clona de um repositório Git, então o primeiro passo é publicar este
projeto num remoto. Depois:

1. **New → Web Service**, apontando para o repositório
2. Runtime **Docker** — o `Dockerfile` na raiz já faz o resto
3. Health check: `/health`
4. Três variáveis de ambiente:

| Variável | Valor |
|---|---|
| `NEMUS_DB` | conexão do Supabase, com `SSL Mode=VerifyFull` |
| `NEMUS_BOOTSTRAP_TOKEN` | gere com `openssl rand -base64 48`; só serve para criar o primeiro acesso |
| `NEMUS_CORS_ORIGINS` | a URL do frontend na Cloudflare |

O `render.yaml` na raiz descreve tudo isso; as três variáveis estão marcadas
como `sync: false`, ou seja, o valor é digitado no painel e **nunca entra no
repositório**.

### Atualizar o frontend na Cloudflare

Já está publicado. Para publicar de novo, de dentro de `web`:

```bash
VITE_NEMUS_API="https://nemus-api.onrender.com" npm run build && npx wrangler deploy
```

Atenção ao `VITE_NEMUS_API` na frente do build: o Vite **incorpora** `VITE_*`
no bundle na hora de compilar. Definir em runtime não tem efeito nenhum, e
buildar sem a variável gera um site que chama a si mesmo em vez da API.

Por isso o build de produção **falha** sem ela — `vite.config.ts` recusa e diz
o que fazer. Vale para `npm run build`, `npm run deploy` e qualquer build de
CI. Foi assim que o primeiro deploy saiu quebrado, e o script `deploy` que o
wrangler criou rodaria exatamente esse build; falhar é melhor do que publicar
quebrado em silêncio. O servidor de desenvolvimento e o `npm run demo` não são
afetados.

O nome em `web/wrangler.jsonc` precisa continuar `nemus`. Se divergir, o
wrangler cria um segundo site em vez de atualizar este.

Se o serviço no Render receber outro nome, duas coisas mudam: o
`VITE_NEMUS_API` acima e o `connect-src` da CSP em `web/public/_headers`.
Esquecer a segunda faz o navegador bloquear as chamadas em silêncio.

### O banco de teste continua sendo local, e isso é proposital

A suíte começa com `DROP SCHEMA public CASCADE`. A barreira em
`PostgresFixture` recusa qualquer host que não seja local, então apontar
`NEMUS_TEST_DB` para o Supabase é rejeitado antes de abrir conexão. Rodar
teste contra o banco hospedado apagaria o schema.

---

## Importação de OFX — onde o formato brasileiro machuca

Quatro coisas quebram um leitor ingênuo de OFX, e as quatro aparecem em
banco brasileiro:

**A tag de valor não é fechada.** OFX 2.x é XML de verdade, mas a 1.x é
SGML — e é essa que Itaú, Bradesco e Santander emitem:

```
<TRNAMT>-45.90
<FITID>202401150001
```

`XDocument` engasga na primeira linha. A saída comum é converter SGML para
XML com expressão regular antes de entregar ao parser, e isso quebra em
toda descrição que contenha `&` ou `<` — que aparece em nome de
estabelecimento mais do que se imagina. Aqui um tokenizador tolerante lê os
dois formatos com o mesmo código: **tag seguida de texto é folha, tag
seguida de tag é agregação**.

**A codificação mente.** O cabeçalho costuma dizer `ENCODING:USASCII` e
`CHARSET:1252` ao mesmo tempo. Sem registrar a página de código, "PADARIA
SÃO JOÃO" chega corrompido — e fica assim no banco para sempre. O cabeçalho
é lido em ASCII primeiro, só para descobrir a codificação real do corpo.

**A vírgula aparece onde a especificação pede ponto.** Parte dos bancos
emite `-45,90`. A detecção usa o último separador, que é o decimal em
qualquer convenção. Nunca passa por `double`: o valor é acumulado dígito a
dígito em `long`.

**A data tem fuso, e converter para UTC estraga o mês.** `DTPOSTED` vem como
`20240131210000[-03:BRT]`. Converter para UTC parece mais correto e é pior:
uma compra às 21h do dia 31 vira dia 1º do mês seguinte, e `occurred_on` é
justamente a competência que o orçamento da fase 4 usa. **Fica a data que o
banco escreveu** — que é a data que a pessoa vê no extrato. Há teste
cobrindo exatamente a virada do mês.

### Duas camadas de idempotência

A do arquivo é o SHA-256 em `import_batches` — atalho barato, não garantia:
dois downloads do mesmo período podem diferir num byte de cabeçalho.

A que garante correção é o **FITID de cada linha**, que compõe a chave junto
do ACCTID. O FITID só é único dentro de uma conta de uma instituição; dois
bancos podem emitir o mesmo, e sem o ACCTID uma linha esconderia a outra.

Reimportar o mesmo arquivo devolve `inserted: 0` e `skippedAsDuplicate` com
o total — dá para ver que nada duplicou, em vez de torcer.

A categorização **não** acontece na importação, de propósito: adivinhar
categoria ali mistura duas responsabilidades e torna o resultado não
reproduzível. Isso é o motor de regras da fase 7, que roda depois e pode ser
reaplicado.

---

## Parcelamento — por que a estrutura é essa

Um "12x sem juros" de R$ 1.200 tem duas verdades ao mesmo tempo:

- **Contábil:** no ato da compra você já deve R$ 1.200 ao cartão. O passivo é
  integral e imediato — **uma** transação, `-120000` na conta do cartão.
- **Orçamentária:** o compromisso consome R$ 100 do orçamento em cada um dos
  12 meses seguintes — a tabela `installments`, um cronograma.

São dimensões diferentes, e é por isso que parcelamento não cabe só no razão.
O banco vai mandar as 12 linhas mesmo assim (ou 1, ou 12 com descrição
`"IFOOD 03/12"` — varia por instituição). Por isso cada **parcela** tem
`external_id` próprio: na fase 6 o importador reconcilia a linha recebida
contra a parcela já prevista, em vez de criar transação nova.

Invariante já valendo: `SUM(installments.amount) = plan.financed_amount`,
exata. R$ 100 em 3x são 33,34 + 33,33 + 33,33. O centavo residual tem que
existir em algum lugar e nunca pode evaporar. Onde ele cai varia por emissor,
então é parâmetro (`RemainderPlacement`); o que não varia é a soma fechar.

---

## Parcelamento — duas verdades ao mesmo tempo

Um "12x sem juros" de R$ 1.200 e duas coisas ao mesmo tempo, e o Nemus grava as
duas separadas porque elas sao separadas:

| | O que e | Onde vive |
|---|---|---|
| Contabil | Voce ja deve R$ 1.200 ao cartao **hoje**. O passivo e integral e imediato | **Uma** transacao no razao: −1.200 no cartao |
| Calendario | O compromisso se espalha por 12 faturas | `installments`: cronograma, **nao** lancamento |

As parcelas nao sao transacoes: elas nao mexem em saldo nenhum. Se fossem, o
mesmo dinheiro apareceria duas vezes — uma na divida do cartao, outra na
parcela. A tela de **Parcelas** mostra os dois lados: quanto ainda falta pagar
e quanto de cada fatura futura ja esta comprometido antes de o mes comecar.

**Juros entram em partida separada.** O produto custou o preco a vista; o resto
e custo de financiamento. Somados numa perna so, voce nunca saberia quanto o
parcelamento custou — que e justamente o que interessa saber.

**Em qual fatura a parcela cai** sai do fechamento do cartao: compra ate o dia
do fechamento entra na fatura do proprio mes; depois dele, na do mes seguinte.
Por isso o cartao precisa ter fechamento e vencimento configurados (em Contas)
antes de aceitar uma compra parcelada — sem eles, as parcelas ficariam
penduradas em nenhum mes.

**O que ainda nao existe:** reconciliar a parcela com a linha que o banco manda
na fatura. Cada parcela ja tem `external_id` proprio esperando isso; e trabalho
da importacao de fatura.

---

## Relatórios — somar onde os dados estão

Até a fase 6 os números de período eram somados no navegador, sobre os
lançamentos que a tela tinha carregado: no máximo 200, que é o teto de página
da API. Com mais do que isso no período, o gráfico de seis meses desenhava a
borda da página, não o gasto — e os meses mais antigos apareciam
**subestimados**. A tela avisava, mas avisar que um número está errado não é o
mesmo que o número estar certo.

`ReportQueries` soma no banco, sobre o razão inteiro. O navegador recebe um
número por mês em vez de um lançamento por linha: a tela fica certa e mais
leve ao mesmo tempo.

**A semântica de leitura não mudou junto com o lugar.** Gasto continua sendo o
que entra nas contas externas de despesa, receita o que sai das de receita —
então transferência entre contas próprias não conta, e estorno se abate
sozinho. Mudar onde soma e o que soma na mesma tacada tornaria impossível
dizer por que um número mudou.

Duas escolhas que tinham alternativa:

**A grade de meses vem de `generate_series`, não dos dados.** Mês sem nenhum
lançamento volta zerado em vez de sumir. Um gráfico que omite o mês vazio cola
novembro em janeiro e mente sobre o intervalo.

**A tendência por categoria é esparsa, a de fluxo é densa.** Categoria × mês
quase todo de zeros seria payload grande para informação nenhuma; quem desenha
a tabela preenche buraco com zero melhor do que o banco transmite zero. Já o
fluxo tem no máximo 120 linhas, e ali a densidade é o que garante o eixo.

**A média divide pelos meses da janela, não pelos meses em que houve gasto.**
Uma conta que veio em dois de seis meses pesa dois sextos no mês típico —
dividir por dois faria despesa ocasional parecer mensal.

---

## Gastos fixos — previsão não é lançamento

O que se repete todo mês: aluguel, internet, academia, assinatura. É a metade
que faltava do caderno de gastos — a outra metade, "os que vou adicionando",
o razão já guarda desde a fase 1.

**Gasto fixo NÃO gera lançamento.** A alternativa — postar a despesa sozinho
no dia do vencimento — foi descartada por dois motivos concretos:

| | O que aconteceria |
|---|---|
| Saldo | Aluguel lançado dia 10 e pago dia 12 deixa a conta errada por dois dias |
| Importação | Quando o OFX entrasse, o mesmo aluguel apareceria **duas vezes** — e o pilar 3 não teria como saber que a linha do banco é o lançamento que o app inventou |

O razão só pode conter o que aconteceu. Isto é previsão, e previsão não é
fato. **Não há coluna de "pago":** seria exatamente o cache que a fase 4
recusou no orçamento — um campo que diverge em silêncio quando um lançamento
antigo é editado, e que ninguém percebe estar errado.

### Como a tela sabe que já veio

Olhando o razão. E aí está a parte interessante: se "Casa" tem aluguel
(R$ 2.800) e condomínio (R$ 650), achar *um* gasto em Casa não diz qual dos
dois chegou. Então o valor desempata — dentro da categoria, cada gasto fixo
fica com o lançamento mais próximo do previsto, e cada lançamento serve a no
máximo um gasto fixo. Dois pagamentos de aluguel no mesmo mês marcam um
aluguel, não dois.

**A tolerância é larga de propósito.** Ela existe para separar R$ 2.800 de
R$ 650, não para auditar centavos. Apertada demais, um aluguel reajustado
apareceria como "ainda não veio" — o erro mais irritante possível, porque
acontece justamente no mês em que a conta mudou e você está olhando para ela.
Quem é marcado como *estimado* (luz, água, gás) tem tolerância maior ainda:
uma conta que dobrou continua sendo a conta de luz.

**O limite conhecido:** um gasto fixo casa com UM lançamento. "Assinaturas"
de R$ 77,80 que na verdade são Netflix e Spotify não casa com nenhuma das
duas — e nem diria qual faltou. São dois gastos fixos, e a lista fica melhor
assim.

### O atalho que justifica o resto

**Atribuir ao orçamento** enche os envelopes do mês com o previsto, em vez de
digitar treze valores que são os mesmos de todo mês. Vários gastos fixos na
mesma categoria somam num envelope só. E ele **nunca sobrescreve** envelope
que já tem valor: apagar em silêncio uma decisão tomada à mão seria o oposto
do método.

---

## Roadmap

1. ~~Razão + modelo de dados~~ ✅
2. ~~Lançamento manual e categorias~~ ✅ (API + interface)
3. ~~Importação de OFX idempotente~~ ✅
4. ~~Orçamento mensal com rollover~~ ✅
5. Sincronização via API do Pluggy
6. ~~Parcelamento de cartão~~ ✅ (compra, cronograma e faturas futuras)
7. ~~Relatórios~~ ✅ (agregação no servidor, gasto por categoria mês a mês)
8. ~~Gastos fixos~~ ✅ (previsão do mês, conferida contra o razão)
9. Motor de regras (categorizar sozinho o que o extrato traz)
