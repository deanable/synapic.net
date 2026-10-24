// The indexer's read surface (paths, states, summaries, config model/store)
// moved to Synapic.Indexer.Contract; these global usings keep the test sources
// reading the same as before the move.
global using Synapic.Indexer.Contract;
global using Synapic.Indexer.Contract.Configuration;
