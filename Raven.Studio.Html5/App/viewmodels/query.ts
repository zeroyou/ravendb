import app = require("durandal/app");
import router = require("plugins/router");
import appUrl = require("common/appUrl");
import viewModelBase = require("viewmodels/viewModelBase");
import getDatabaseStatsCommand = require("commands/getDatabaseStatsCommand");
import getIndexDefinitionCommand = require("commands/getIndexDefinitionCommand");
import aceEditorBindingHandler = require("common/aceEditorBindingHandler");
import pagedList = require("common/pagedList");
import pagedResultSet = require("common/pagedResultSet");
import queryIndexCommand = require("commands/queryIndexCommand");
import moment = require("moment");
import deleteIndexesConfirm = require("viewmodels/deleteIndexesConfirm");
import querySort = require("models/querySort");
import getTransformersCommand = require("commands/getTransformersCommand");
import deleteDocumentsMatchingQueryConfirm = require("viewmodels/deleteDocumentsMatchingQueryConfirm");
import getStoredQueriesCommand = require("commands/getStoredQueriesCommand");
import saveDocumentCommand = require("commands/saveDocumentCommand");
import document = require("models/document");

class query extends viewModelBase {

    selectedIndex = ko.observable<string>();
    indexNames = ko.observableArray<string>();
    editIndexUrl: KnockoutComputed<string>;
    termsUrl: KnockoutComputed<string>;
    statsUrl: KnockoutComputed<string>;
    hasSelectedIndex: KnockoutComputed<boolean>;
    queryText = ko.observable("");
    queryResults = ko.observable<pagedList>();
    selectedResultIndices = ko.observableArray<number>();
    queryStats = ko.observable<indexQueryResultsDto>();
    selectedIndexEditUrl: KnockoutComputed<string>;
    sortBys = ko.observableArray<querySort>();
    indexFields = ko.observableArray<string>();
    transformer = ko.observable<string>();
    allTransformers = ko.observableArray<transformerDto>();
    isDefaultOperatorOr = ko.observable(true);
    showFields = ko.observable(false);
    indexEntries = ko.observable(false);
    recentQueries = ko.observableArray<storedQueryDto>();
    recentQueriesDoc = ko.observable<storedQueryContainerDto>();

    static containerSelector = "#queryContainer";

    constructor() {
        super();

        this.editIndexUrl = ko.computed(() => this.selectedIndex() ? appUrl.forEditIndex(this.selectedIndex(), this.activeDatabase()) : null);
        this.termsUrl = ko.computed(() => this.selectedIndex() ? appUrl.forTerms(this.selectedIndex(), this.activeDatabase()) : null);
        this.statsUrl = ko.computed(() => appUrl.forStatus(this.activeDatabase()));
        this.hasSelectedIndex = ko.computed(() => this.selectedIndex() != null);
        this.selectedIndexEditUrl = ko.computed(() => this.selectedIndex() ? appUrl.forEditIndex(this.selectedIndex(), this.activeDatabase()) : '');
        
        aceEditorBindingHandler.install();        
    }

    activate(indexNameOrRecentQueryIndex?: string) {
        super.activate(indexNameOrRecentQueryIndex);

        this.fetchAllIndexes(indexNameOrRecentQueryIndex);
        this.fetchAllTransformers();
        this.fetchRecentQueries(indexNameOrRecentQueryIndex);
    }

    attached() {
        this.useBootstrapTooltips();
        this.createKeyboardShortcut("F2", () => this.editSelectedIndex(), query.containerSelector);
        $("#indexQueryLabel").popover({
            html: true,
            trigger: 'hover',
            container: '.form-horizontal',
            content: 'Queries use Lucene syntax. Examples:<pre><span class="code-keyword">Name</span>: Hi?berna*<br/><span class="code-keyword">Count</span>: [0 TO 10]<br/><span class="code-keyword">Title</span>: "RavenDb Queries 1010" AND <span class="code-keyword">Price</span>: [10.99 TO *]</pre>',
        });
    }

    deactivate() {
        super.deactivate();
        this.removeKeyboardShortcuts(query.containerSelector);
    }

    editSelectedIndex() {
        router.navigate(this.editIndexUrl());
    }

    fetchAllIndexes(indexNameOrRecentQueryIndex?: string) {
        new getDatabaseStatsCommand(this.activeDatabase())
            .execute()
            .done((stats: databaseStatisticsDto) => {
                this.indexNames(stats.Indexes.map(i => i.PublicName));
                if (!indexNameOrRecentQueryIndex) {
                    this.setSelectedIndex(this.indexNames.first());
                } else if (this.indexNames.contains(indexNameOrRecentQueryIndex)) {
                    this.setSelectedIndex(indexNameOrRecentQueryIndex);
                }
            });
    }

    fetchRecentQueries(indexNameOrRecentQueryIndex?: string) {
        new getStoredQueriesCommand(this.activeDatabase())
            .execute()
            .fail(_ => {
                var newStoredQueryContainer: storedQueryContainerDto = {
                    '@metadata': {},
                    Queries: []
                }
                this.recentQueriesDoc(newStoredQueryContainer);
                this.recentQueries(newStoredQueryContainer.Queries);
            })
            .done((doc: document) => {
                var dto = <storedQueryContainerDto>doc.toDto(true);
                this.recentQueriesDoc(dto);
                this.recentQueries(dto.Queries);

                // Select one if we're configured to do so.
                if (indexNameOrRecentQueryIndex && indexNameOrRecentQueryIndex.indexOf("recentquery-") === 0) {
                    var recentQueryToSelectIndex = parseInt(indexNameOrRecentQueryIndex.substr("recentquery-".length), 10);
                    if (!isNaN(recentQueryToSelectIndex) && recentQueryToSelectIndex < dto.Queries.length) {
                        this.runRecentQuery(dto.Queries[recentQueryToSelectIndex]);
                    }
                }
            });
    }

    fetchAllTransformers() {
        new getTransformersCommand(this.activeDatabase())
            .execute()
            .done((results: transformerDto[]) => this.allTransformers(results));
    }

    runRecentQuery(query: storedQueryDto) {
        this.selectedIndex(query.IndexName);
        this.queryText(query.QueryText);
        this.showFields(query.ShowFields);
        this.indexEntries(query.IndexEntries);
        this.isDefaultOperatorOr(query.UseAndOperator === false);
        this.transformer(query.TransformerName);
        this.sortBys(query.Sorts.map(s => querySort.fromQuerySortString(s)));
        this.runQuery();
    }

    runQuery(): pagedList {
        var selectedIndex = this.selectedIndex();
        if (selectedIndex) {
            var queryText = this.queryText();
            var sorts = this.sortBys().filter(s => s.fieldName() != null);
            var database = this.activeDatabase();
            var transformer = this.transformer();
            var showFields = this.showFields();
            var indexEntries = this.indexEntries();
            var useAndOperator = this.isDefaultOperatorOr() === false;
            var resultsFetcher = (skip: number, take: number) => {
                var command = new queryIndexCommand(selectedIndex, database, skip, take, queryText, sorts, transformer, showFields, indexEntries, useAndOperator);
                return command
                    .execute()
                    .done((queryResults: pagedResultSet) => this.queryStats(queryResults.additionalResultInfo));
            };
            var resultsList = new pagedList(resultsFetcher);
            this.queryResults(resultsList);
            this.recordQueryRun(selectedIndex, queryText, sorts.map(s => s.toQuerySortString()), transformer, showFields, indexEntries, useAndOperator);

            return resultsList;
        }

        return null;
    }

    recordQueryRun(indexName: string, queryText: string, sorts: string[], transformer: string, showFields: boolean, indexEntries: boolean, useAndOperator: boolean) {
        var newQuery: storedQueryDto = {
            IndexEntries: indexEntries,
            IndexName: indexName,
            IsPinned: false,
            QueryText: queryText,
            ShowFields: showFields,
            Sorts: sorts,
            TransformerName: transformer || null,
            UseAndOperator: useAndOperator
        };

        var existing = this.recentQueries.first(q => query.areSameQueriesIgnoringPinned(q, newQuery));
        if (existing) {
            // Move it to the top of the list.
            this.recentQueries.remove(existing);
            this.recentQueries.unshift(existing);
        } else {
            this.recentQueries.unshift(newQuery);
        }

        // Limit us to 15 query recent runs.
        if (this.recentQueries().length > 15) {
            this.recentQueries.remove(this.recentQueries()[15]);
        }

        var recentQueriesDoc = this.recentQueriesDoc();
        if (recentQueriesDoc) {
            recentQueriesDoc.Queries = this.recentQueries();
            var preppedDoc = new document(recentQueriesDoc);
            new saveDocumentCommand(getStoredQueriesCommand.storedQueryDocId, preppedDoc, this.activeDatabase(), false)
                .execute()
                .done((result: { Key: string; ETag: string; }) => recentQueriesDoc['@metadata']['@etag'] = result.ETag);
        }
    }

    getRecentQuerySortsText(recentQueryIndex: number) {
        var sorts = this.recentQueries()[recentQueryIndex].Sorts;
        if (sorts.length === 0) {
            return "";
        }
        return sorts
            .map(s => querySort.fromQuerySortString(s))
            .map(s => s.toHumanizedString())
            .reduce((first, second) => first + ", " + second);
    }

    static areSameQueriesIgnoringPinned(first: storedQueryDto, second: storedQueryDto) {
        return first.IndexEntries === second.IndexEntries &&
            first.IndexName === second.IndexName &&
            first.QueryText === second.QueryText &&
            first.ShowFields === second.ShowFields &&
            first.Sorts.length === second.Sorts.length &&
            first.Sorts.every((firstSort, index) => firstSort === second.Sorts[index]) &&
            first.TransformerName === second.TransformerName &&
            first.UseAndOperator === second.UseAndOperator;
    }

    setSelectedIndex(indexName: string) {
        this.sortBys.removeAll();
        this.selectedIndex(indexName);
        this.runQuery();

        // Fetch the index definition so that we get an updated list of fields.
        new getIndexDefinitionCommand(indexName, this.activeDatabase())
            .execute()
            .done((result: indexDefinitionContainerDto) => {
                this.indexFields(result.Index.Fields);
            });

        // Reflect the new index in the address bar.
        var url = appUrl.forQuery(this.activeDatabase(), indexName);
        var navOptions: DurandalNavigationOptions = {
            replace: true,
            trigger: false
        };
        router.navigate(url, navOptions);
        NProgress.done();
    }

    addSortBy() {
        var sort = new querySort();
        sort.fieldName.subscribe(() => this.runQuery());
        sort.sortDirection.subscribe(() => this.runQuery());
        this.sortBys.push(sort);
    }

    removeSortBy(sortBy: querySort) {
        this.sortBys.remove(sortBy);
        this.runQuery();
    }

    addTransformer() {
        this.transformer("");
    }

    selectTransformer(transformer: transformerDto) {
        this.transformer(transformer.name);
        this.runQuery();
    }

    removeTransformer() {
        this.transformer(null);
        this.runQuery();
    }

    setOperatorOr() {
        this.isDefaultOperatorOr(true);
        this.runQuery();
    }

    setOperatorAnd() {
        this.isDefaultOperatorOr(false);
        this.runQuery();
    }

    toggleShowFields() {
        this.showFields(!this.showFields());
        this.runQuery();
    }

    toggleIndexEntries() {
        this.indexEntries(!this.indexEntries());
        this.runQuery();
    }

    deleteDocsMatchingQuery() {
        // Run the query so that we have an idea of what we'll be deleting.
        var queryResult = this.runQuery();
        queryResult
            .fetch(0, 1)
            .done((results: pagedResultSet) => {
                if (results.totalResultCount === 0) {
                    app.showMessage("There are no documents matching your query.", "Nothing to do");
                } else {
                    this.promptDeleteDocsMatchingQuery(results.totalResultCount);
                }
            });
    }

    promptDeleteDocsMatchingQuery(resultCount: number) {
        var viewModel = new deleteDocumentsMatchingQueryConfirm(this.selectedIndex(), this.queryText(), resultCount, this.activeDatabase());
        app
            .showDialog(viewModel)
            .done(() => this.runQuery());
    }
}

export = query;