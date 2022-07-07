using System.Text;
using Markdig;
using Markdig.Renderers.Normalize;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Meilidown.Common;
using Meilisearch;

namespace Meilidown.Indexer
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var files = GatherFiles();
                var indexedFiles = ProcessFiles(files);
                await UpdateIndex(indexedFiles, stoppingToken);

#if DEBUG
                Console.ReadKey();
#else
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
#endif
            }
        }

        private IEnumerable<RepositoryFile> GatherFiles()
        {
            foreach (var repository in RepositoryRepository.GetRepositories(_configuration))
            {
                repository.Update();

                foreach (var repositoryFile in repository.FindFiles("**.md"))
                {
                    yield return repositoryFile;
                }
            }
        }

        private IEnumerable<IndexedFile> ProcessFiles(IEnumerable<RepositoryFile> files)
        {
            var markdownPipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .UseEmojiAndSmiley()
                .UseYamlFrontMatter()
                .UseDiagrams()
                .Build();

            foreach (var f in files)
            {
                _logger.LogInformation("Processing {File}", f.Location);

                var content = File.ReadAllText(f.AbsolutePath);
                var document = Markdown.Parse(content, markdownPipeline);

                UpdateImageLinks(f, document);
                
                var builder = new StringBuilder();
                var writer = new StringWriter(builder);
                var renderer = new NormalizeRenderer(writer, new()
                {
                    ExpandAutoLinks = false,
                });
                renderer.Render(document);

                yield return new(
                    f.Uid,
                    f.Name,
                    builder.ToString(),
                    0,
                    f.Location
                );
            }
        }

        private void UpdateImageLinks(RepositoryFile file, MarkdownObject markdownObject)
        {
            foreach (var child in markdownObject.Descendants())
            {
                if (child is not LinkInline { IsImage: true } link) continue;

                var location = string.Join('/', file.RelativePath.Split(Path.PathSeparator).SkipLast(1)) + '/' + link.Url;

                link.Url = string.Format(_configuration["FileApi:Image"], Uri.EscapeDataString(location));
                if (link.Reference != null) link.Reference.Url = null;
            }
        }

        private async Task UpdateIndex(IEnumerable<IndexedFile> indexedFiles, CancellationToken cancellationToken)
        {
            var client = new MeilisearchClient(_configuration["Meilisearch:Url"], _configuration["Meilisearch:ApiKey"]);
            var health = await client.HealthAsync(cancellationToken);
            _logger.LogInformation("Meilisearch is {Status}", health.Status);

            var settings = new Settings
            {
                FilterableAttributes = new[] { "uid", "name", "location", "content" },
                SortableAttributes = new[] { "name", "order", "location" },
                SearchableAttributes = new[] { "name", "location", "content" },
            };

            var filesIndex = client.Index("files");
            var tasks = new Dictionary<string, Task<TaskInfo>>
            {
                { "Delete previous index", filesIndex.DeleteAllDocumentsAsync(cancellationToken) },
                { "Add new index", filesIndex.AddDocumentsAsync(indexedFiles, cancellationToken: cancellationToken) },
                { "Update index settings", filesIndex.UpdateSettingsAsync(settings, cancellationToken) },
            };

            foreach (var task in tasks)
            {
                var info = await task.Value;
                var result = await client.WaitForTaskAsync(info.Uid, cancellationToken: cancellationToken);
                _logger.LogInformation("Task '{Name}': {Status}", task.Key, result.Status);

                if (result.Error == null) continue;

                foreach (var e in result.Error.Values)
                {
                    _logger.LogError("{@Error}", e);
                }
            }
        }
    }
}