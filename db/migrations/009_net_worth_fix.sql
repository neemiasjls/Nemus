-- Nemus 009 - Correcao: patrimonio liquido nao inclui EQUITY.
--
-- O ERRO. A 007 definiu net_worth como a soma de TODAS as contas internas -
-- e EQUITY e interna. So que "Saldos iniciais" e a CONTRAPARTIDA do
-- patrimonio, nao parte dele: abrir uma conta corrente com R$ 1.000 lanca
-- +1.000 na conta e -1.000 em Saldos iniciais, e as duas somadas dao zero.
-- A view mostrava patrimonio ZERO para quem tinha mil reais.
--
-- COMO APARECEU. Nenhum teste olhava v_net_worth. Apareceu construindo o
-- painel, que poe esse numero no topo da tela. O teste que faltava entrou
-- junto desta migration (NetWorthTests).
--
-- A CORRECAO. Patrimonio liquido e ativo mais passivo - o passivo ja vem
-- negativo, entao a soma direta e "o que voce tem menos o que voce deve". A
-- conta de ajuste de conciliacao tambem e EQUITY e tambem fica de fora, pelo
-- mesmo motivo: o ajuste ja moveu a conta bancaria, e e la que o efeito real
-- aparece.
--
-- WITH (security_invoker = true) E OBRIGATORIO AQUI. CREATE OR REPLACE VIEW
-- substitui as opcoes da view pelas que vierem na instrucao. Sem repetir a
-- opcao, esta migration desfaria em silencio o que a 008 fez, e a view
-- voltaria a rodar com a permissao de quem a criou - o tunel por onde o RLS
-- vaza. NetWorthTests confere isso.
--
-- So a view muda. Nenhuma tabela, nenhum dado.

CREATE OR REPLACE VIEW v_net_worth
WITH (security_invoker = true)
AS
SELECT currency_code,
       COALESCE(SUM(balance) FILTER (WHERE type_code = 'ASSET'), 0)::BIGINT     AS assets,
       COALESCE(SUM(balance) FILTER (WHERE type_code = 'LIABILITY'), 0)::BIGINT AS liabilities,
       COALESCE(SUM(balance) FILTER (WHERE type_code IN ('ASSET', 'LIABILITY')), 0)::BIGINT
                                                                                AS net_worth
  FROM v_account_balances
 GROUP BY currency_code;

COMMENT ON VIEW v_net_worth IS
    'Patrimonio liquido = ativos + passivos (passivo tem saldo negativo). EQUITY fica de fora: e a contrapartida do patrimonio, nao parte dele.';
