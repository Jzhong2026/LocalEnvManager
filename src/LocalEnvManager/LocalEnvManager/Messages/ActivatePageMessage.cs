namespace LocalEnvManager.Messages
{
    /// <summary>Requests navigation to a top-level page by its stable order.</summary>
    public sealed class ActivatePageMessage
    {
        public ActivatePageMessage(int pageOrder) => PageOrder = pageOrder;

        public int PageOrder { get; }
    }
}
