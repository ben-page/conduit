using System.Text;
using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public class GetExportFileHandler : IQueryHandler<GetExportFileQuery, ConduitFile?>
{
    public const string Csv = "id,name\n1,alpha\n2,beta\n";
    public const string FileName = "export-2026-04.csv";

    public Task<ConduitFile?> HandleAsync(GetExportFileQuery query, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromBytes(Encoding.UTF8.GetBytes(Csv), "text/csv");
        return Task.FromResult<ConduitFile?>(new ConduitFile(content, FileName));
    }
}
