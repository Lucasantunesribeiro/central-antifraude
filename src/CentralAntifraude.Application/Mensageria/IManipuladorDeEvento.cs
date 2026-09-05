using CentralAntifraude.Domain.Eventos;

namespace CentralAntifraude.Application.Mensageria;

/// <summary>
/// Um efeito que uma mensagem provoca.
///
/// **Por que esta interface nasce na Fase 6.** Ate a Fase 5 havia um efeito
/// so, e ele morava dentro do worker. Agora sao dois — a projecao diaria e o
/// alerta — e os dois precisam acontecer para a MESMA mensagem, cada um uma
/// vez.
///
/// Isso e fan-out, e existem duas formas de faze-lo. A primeira e publicar a
/// mensagem em duas filas, uma por consumidor: e o que se faz quando os
/// consumidores rodam em processos separados, e no SQS exige um topico na
/// frente. A segunda e um processo que le uma vez e aplica os dois efeitos na
/// mesma transacao. A Central Antifraude e um **monolito modular**
/// (`CLAUDE.md` secao 26), os dois efeitos gravam no mesmo banco, e nenhum
/// deles chama servico externo — entao a segunda forma resolve o problema real
/// sem infraestrutura nova. Publicar duas vezes daria duas chances de
/// divergir, e nenhuma vantagem.
///
/// **Cada manipulador tem seu proprio nome na Inbox.** A chave de unicidade e
/// (consumidor, evento), e nao so o evento — marcar apenas pelo evento faria o
/// segundo manipulador achar que o trabalho dele ja tinha sido feito. E o que
/// permite, na Fase 14, mover um deles para outro processo sem mudar a
/// semantica de idempotencia.
/// </summary>
public interface IManipuladorDeEvento
{
    /// <summary>
    /// Nome deste consumidor na Inbox. Faz parte da chave de unicidade, e por
    /// isso **nunca pode ser renomeado**: mudar o nome faz o mundo inteiro
    /// parecer nao processado, e todo evento retido volta a aplicar efeito.
    /// </summary>
    string Consumidor { get; }

    /// <summary>
    /// Aplica o efeito.
    ///
    /// Roda dentro da transacao aberta pelo worker, junto da gravacao da
    /// Inbox. Nao abre transacao propria, nao confirma nada e nao apaga
    /// mensagem: quem coordena isso e o worker.
    ///
    /// Um manipulador que nao reconheca o conteudo do envelope simplesmente
    /// nao faz nada — a validacao de tipo, versao e tenant ja aconteceu antes
    /// de a mensagem chegar aqui.
    /// </summary>
    Task AplicarAsync(EnvelopeDeEvento envelope, CancellationToken cancellationToken);
}
