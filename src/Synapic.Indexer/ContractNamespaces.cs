// The read-only surface of the indexer (paths, queue states and summaries, the
// configuration model and store) lives in Synapic.Indexer.Contract so the
// desktop app and the service share one type set instead of agreeing by
// convention. These global usings keep the service's own sources reading the
// same as before the move; a file that names a moved type needs no edit.
global using Synapic.Indexer.Contract;
global using Synapic.Indexer.Contract.Configuration;
