using System;
using System.Collections.Generic;
using System.Linq;

namespace MssqlIntelliSense.Core.Ai;

/// <summary>
/// Managed Hierarchical Navigable Small World index for cosine-similarity vectors.
/// Kept in Core so the SSMS net472 host does not need a native vector-search DLL.
/// </summary>
internal sealed class HnswVectorIndex
{
    private readonly List<Node> _nodes = new();
    private readonly int _maxConnections;
    private readonly int _constructionCandidates;
    private int _entryPoint = -1;
    private int _maxLevel = -1;
    private int _dimensions;

    public HnswVectorIndex(int maxConnections = 12, int constructionCandidates = 64)
    {
        if (maxConnections < 2) throw new ArgumentOutOfRangeException(nameof(maxConnections));
        if (constructionCandidates < maxConnections) throw new ArgumentOutOfRangeException(nameof(constructionCandidates));

        _maxConnections = maxConnections;
        _constructionCandidates = constructionCandidates;
    }

    public int Count => _nodes.Count;

    public HnswVectorIndexSnapshot ExportSnapshot()
    {
        return new HnswVectorIndexSnapshot(
            _maxConnections,
            _constructionCandidates,
            _entryPoint,
            _maxLevel,
            _dimensions,
            _nodes.Select(node => new HnswVectorNodeSnapshot(
                node.Vector,
                node.Level,
                node.Neighbors.Select(neighbors => neighbors.ToArray()).ToArray())).ToArray());
    }

    public static HnswVectorIndex ImportSnapshot(HnswVectorIndexSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.Dimensions < 0) throw new InvalidOperationException("The HNSW snapshot has invalid dimensions.");
        if (snapshot.Nodes == null) throw new InvalidOperationException("The HNSW snapshot is missing nodes.");

        var index = new HnswVectorIndex(snapshot.MaxConnections, snapshot.ConstructionCandidates)
        {
            _entryPoint = snapshot.EntryPoint,
            _maxLevel = snapshot.MaxLevel,
            _dimensions = snapshot.Dimensions
        };

        foreach (var nodeSnapshot in snapshot.Nodes)
        {
            if (nodeSnapshot.Vector == null || nodeSnapshot.Vector.Length != snapshot.Dimensions ||
                nodeSnapshot.Level < 0 || nodeSnapshot.Neighbors == null || nodeSnapshot.Neighbors.Length != nodeSnapshot.Level + 1)
            {
                throw new InvalidOperationException("The HNSW snapshot contains an invalid node.");
            }

            index._nodes.Add(new Node(nodeSnapshot.Vector, nodeSnapshot.Level));
        }

        for (var nodeId = 0; nodeId < snapshot.Nodes.Length; nodeId++)
        {
            var nodeSnapshot = snapshot.Nodes[nodeId];
            for (var level = 0; level < nodeSnapshot.Neighbors.Length; level++)
            {
                var neighbors = nodeSnapshot.Neighbors[level];
                if (neighbors == null || neighbors.Any(neighborId => neighborId < 0 || neighborId >= index._nodes.Count))
                {
                    throw new InvalidOperationException("The HNSW snapshot contains an invalid neighbor reference.");
                }

                index._nodes[nodeId].Neighbors[level].AddRange(neighbors.Distinct());
            }
        }

        if (index._nodes.Count == 0)
        {
            if (index._entryPoint != -1 || index._maxLevel != -1 || index._dimensions != 0)
            {
                throw new InvalidOperationException("The empty HNSW snapshot has invalid metadata.");
            }
        }
        else if (index._entryPoint < 0 || index._entryPoint >= index._nodes.Count || index._maxLevel < 0)
        {
            throw new InvalidOperationException("The HNSW snapshot has an invalid entry point.");
        }

        return index;
    }

    public void Add(float[] vector)
    {
        if (vector == null || vector.Length == 0) throw new ArgumentException("A non-empty vector is required.", nameof(vector));
        if (_dimensions != 0 && vector.Length != _dimensions) throw new ArgumentException("All vectors must have the same dimensions.", nameof(vector));

        _dimensions = vector.Length;
        var nodeId = _nodes.Count;
        var level = GetLevel(nodeId);
        var node = new Node(vector, level);
        _nodes.Add(node);

        for (var currentLevel = 0; currentLevel <= level; currentLevel++)
        {
            var nearest = _nodes
                .Take(nodeId)
                .Select((candidate, candidateId) => new SearchResult(candidateId, CosineSimilarity(vector, candidate.Vector)))
                .Where(candidate => candidateIdHasLevel(candidate.Id, currentLevel))
                .OrderByDescending(candidate => candidate.Score)
                .Take(_constructionCandidates)
                .ToArray();

            foreach (var candidate in nearest.Take(_maxConnections))
            {
                Connect(nodeId, candidate.Id, currentLevel);
            }
        }

        if (level > _maxLevel)
        {
            _entryPoint = nodeId;
            _maxLevel = level;
        }

        bool candidateIdHasLevel(int candidateId, int currentLevel) => _nodes[candidateId].Level >= currentLevel;
    }

    public IReadOnlyList<SearchResult> Search(float[] query, int limit, int explorationCandidates = 64)
    {
        if (query == null || query.Length == 0 || _nodes.Count == 0 || limit <= 0) return Array.Empty<SearchResult>();
        if (query.Length != _dimensions) throw new ArgumentException("Query vector dimensions do not match the index.", nameof(query));

        var current = _entryPoint;
        var currentScore = CosineSimilarity(query, _nodes[current].Vector);
        for (var level = _maxLevel; level > 0; level--)
        {
            var improved = true;
            while (improved)
            {
                improved = false;
                foreach (var neighbor in _nodes[current].Neighbors[level])
                {
                    var neighborScore = CosineSimilarity(query, _nodes[neighbor].Vector);
                    if (neighborScore > currentScore)
                    {
                        current = neighbor;
                        currentScore = neighborScore;
                        improved = true;
                    }
                }
            }
        }

        return SearchLayer(query, current, Math.Max(limit, explorationCandidates), 0)
            .OrderByDescending(result => result.Score)
            .Take(limit)
            .ToArray();
    }

    private IReadOnlyList<SearchResult> SearchLayer(float[] query, int entryPoint, int explorationCandidates, int level)
    {
        var visited = new HashSet<int> { entryPoint };
        var candidates = new List<SearchResult>
        {
            new SearchResult(entryPoint, CosineSimilarity(query, _nodes[entryPoint].Vector))
        };
        var results = new List<SearchResult>(candidates);

        while (candidates.Count > 0)
        {
            candidates.Sort((left, right) => right.Score.CompareTo(left.Score));
            var candidate = candidates[0];
            candidates.RemoveAt(0);

            results.Sort((left, right) => right.Score.CompareTo(left.Score));
            if (results.Count >= explorationCandidates && candidate.Score < results[results.Count - 1].Score)
            {
                break;
            }

            foreach (var neighbor in _nodes[candidate.Id].Neighbors[level])
            {
                if (!visited.Add(neighbor)) continue;

                var result = new SearchResult(neighbor, CosineSimilarity(query, _nodes[neighbor].Vector));
                candidates.Add(result);
                results.Add(result);
                results.Sort((left, right) => right.Score.CompareTo(left.Score));
                if (results.Count > explorationCandidates)
                {
                    results.RemoveRange(explorationCandidates, results.Count - explorationCandidates);
                }
            }
        }

        return results;
    }

    private void Connect(int sourceId, int targetId, int level)
    {
        AddNeighbor(sourceId, targetId, level);
        AddNeighbor(targetId, sourceId, level);
    }

    private void AddNeighbor(int nodeId, int neighborId, int level)
    {
        var neighbors = _nodes[nodeId].Neighbors[level];
        if (!neighbors.Contains(neighborId))
        {
            neighbors.Add(neighborId);
        }

        if (neighbors.Count <= _maxConnections) return;

        var vector = _nodes[nodeId].Vector;
        var retained = neighbors
            .Select(id => new SearchResult(id, CosineSimilarity(vector, _nodes[id].Vector)))
            .OrderByDescending(result => result.Score)
            .Take(_maxConnections)
            .Select(result => result.Id)
            .ToArray();
        neighbors.Clear();
        neighbors.AddRange(retained);
    }

    private static int GetLevel(int nodeId)
    {
        unchecked
        {
            var value = (uint)((nodeId + 1) * 2654435761);
            var level = 0;
            while ((value & 15) == 0 && level < 8)
            {
                level++;
                value >>= 4;
            }
            return level;
        }
    }

    private static float CosineSimilarity(float[] left, float[] right)
    {
        double dotProduct = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dotProduct += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        var denominator = Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude);
        return denominator <= 0 ? 0 : (float)(dotProduct / denominator);
    }

    internal sealed record SearchResult(int Id, float Score);

    private sealed class Node
    {
        public Node(float[] vector, int level)
        {
            Vector = vector;
            Level = level;
            Neighbors = Enumerable.Range(0, level + 1).Select(_ => new List<int>()).ToArray();
        }

        public float[] Vector { get; }
        public int Level { get; }
        public List<int>[] Neighbors { get; }
    }
}

internal sealed record HnswVectorIndexSnapshot(
    int MaxConnections,
    int ConstructionCandidates,
    int EntryPoint,
    int MaxLevel,
    int Dimensions,
    HnswVectorNodeSnapshot[] Nodes);

internal sealed record HnswVectorNodeSnapshot(
    float[] Vector,
    int Level,
    int[][] Neighbors);

