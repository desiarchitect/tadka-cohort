namespace Tadka.Api.Exceptions;

public class NotFoundException : Exception
{
    public NotFoundException(string entityName, object id)
        : base($"{entityName} with id '{id}' was not found.")
    {
        EntityName = entityName;
    }

    public string EntityName { get; }
}
