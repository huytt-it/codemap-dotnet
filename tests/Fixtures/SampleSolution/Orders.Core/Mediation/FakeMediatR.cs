namespace Orders.Mediation;

/// <summary>Stand-in for MediatR — see FakeMvc.cs for why fakes work here (matching is BY NAME, not by resolving the real package).</summary>
public interface IRequest<TResponse>
{
}

public interface IRequestHandler<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    TResponse Handle(TRequest request);
}

public interface IMediator
{
    TResponse Send<TResponse>(IRequest<TResponse> request);
}
