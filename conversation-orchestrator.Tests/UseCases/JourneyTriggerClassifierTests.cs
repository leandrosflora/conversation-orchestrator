using conversation_orchestrator.Application.UseCases;
using Xunit;

namespace conversation_orchestrator.Tests.UseCases;

public class JourneyTriggerClassifierTests
{
    [Theory]
    [InlineData("iniciar_renegociacao", JourneyTrigger.RequestedRenegotiation)]
    [InlineData("RENEGOCIACAO_REQUEST", JourneyTrigger.RequestedRenegotiation)]
    [InlineData("informar_cpf", JourneyTrigger.ProvidedIdentification)]
    [InlineData("identification_provided", JourneyTrigger.ProvidedIdentification)]
    [InlineData("selecionar_contrato", JourneyTrigger.SelectedContract)]
    [InlineData("CONTRATO_SELECIONADO", JourneyTrigger.SelectedContract)]
    [InlineData("consultar_elegibilidade", JourneyTrigger.EligibilityConfirmed)]
    [InlineData("ELEGIBILIDADE_CHECK", JourneyTrigger.EligibilityConfirmed)]
    [InlineData("solicitar_simulacao", JourneyTrigger.RequestedSimulation)]
    [InlineData("SIMULACAO_SOLICITADA", JourneyTrigger.RequestedSimulation)]
    [InlineData("proposta_apresentada", JourneyTrigger.ProposalPresented)]
    [InlineData("proposal_presented", JourneyTrigger.ProposalPresented)]
    [InlineData("aceitar_proposta", JourneyTrigger.SelectedProposal)]
    [InlineData("escolher_opcao", JourneyTrigger.SelectedProposal)]
    [InlineData("confirmar_acordo", JourneyTrigger.ConfirmedAgreement)]
    [InlineData("confirmation", JourneyTrigger.ConfirmedAgreement)]
    [InlineData("cancelar_negociacao", JourneyTrigger.RequestedCancellation)]
    [InlineData("desistir", JourneyTrigger.RequestedCancellation)]
    public void Classify_RecognizedIntent_ReturnsExpectedTrigger(string intent, JourneyTrigger expected)
    {
        var result = JourneyTriggerClassifier.Classify(intent);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("saudacao")]
    [InlineData("greeting")]
    [InlineData("faq")]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_UnrecognizedIntent_ReturnsNone(string intent)
    {
        var result = JourneyTriggerClassifier.Classify(intent);

        Assert.Equal(JourneyTrigger.None, result);
    }

    [Fact]
    public void Classify_NullIntent_ReturnsNone()
    {
        var result = JourneyTriggerClassifier.Classify(null);

        Assert.Equal(JourneyTrigger.None, result);
    }
}
