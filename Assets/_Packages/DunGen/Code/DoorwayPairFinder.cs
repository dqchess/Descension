﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace DunGen
{
	#region Helper Types

	public struct DoorwayPair
	{
		public Tile PreviousTile { get; private set; }
		public Doorway PreviousDoorway { get; private set; }
		public PreProcessTileData NextTemplate { get; private set; }
		public Doorway NextDoorway { get; private set; }
		public TileSet NextTileSet { get; private set; }
		public float TileWeight { get; private set; }
		public float DoorwayWeight { get; private set; }


		public DoorwayPair(Tile previousTile, Doorway previousDoorway, PreProcessTileData nextTemplate, Doorway nextDoorway, TileSet nextTileSet, float tileWeight, float doorwayWeight)
		{
			PreviousTile = previousTile;
			PreviousDoorway = previousDoorway;
			NextTemplate = nextTemplate;
			NextDoorway = nextDoorway;
			NextTileSet = nextTileSet;
			TileWeight = tileWeight;
			DoorwayWeight = doorwayWeight;
		}
	}

	#endregion

	public delegate bool TileMatchDelegate(Tile previousTile, GameObject previousPrefab, Tile potentialNextTile, GameObject potentialNextPrefab, ref float weight);
	public delegate PreProcessTileData GetTileTemplateDelegate(GameObject prefab);

	public sealed class DoorwayPairFinder
	{
		public System.Random RandomStream;
		public List<GameObjectChance> TileWeights;
		public Tile PreviousTile;
		public GameObject PreviousPrefab;
		public bool IsOnMainPath;
		public float NormalizedDepth;
		public DungeonArchetype Archetype;
		public bool? AllowRotation;
		public Vector3 UpVector;
		public TileMatchDelegate IsTileAllowedPredicate;
		public GetTileTemplateDelegate GetTileTemplateDelegate;

		private List<GameObjectChance> tileOrder;


		public Queue<DoorwayPair> GetDoorwayPairs(int? maxCount)
		{
			tileOrder = CalculateOrderedListOfTiles();

			List<DoorwayPair> potentialPairs;

			if (PreviousTile == null)
				potentialPairs = GetPotentialDoorwayPairsForFirstTile().ToList();
			else
				potentialPairs = GetPotentialDoorwayPairsForNonFirstTile().ToList();

			int count = potentialPairs.Count;

			if (maxCount.HasValue)
				count = Math.Min(count, maxCount.Value);

			Queue<DoorwayPair> pairs = new Queue<DoorwayPair>(count);
			foreach (var pair in OrderDoorwayPairs(potentialPairs, count))//potentialPairs.OrderByDescending(x => x.TileWeight).ThenByDescending(x => x.DoorwayWeight).Take(count))
				pairs.Enqueue(pair);

			return pairs;
		}

		private int CompareDoorwaysTileWeight(DoorwayPair x, DoorwayPair y)
		{
			// Reversed to sort with highest TileWeight value first
			return y.TileWeight.CompareTo(x.TileWeight);
		}

		private IEnumerable<DoorwayPair> OrderDoorwayPairs(List<DoorwayPair> potentialPairs, int count)
		{
			potentialPairs.Sort(CompareDoorwaysTileWeight);

			// Then order by DoorwayWeight. LINQ ThenByDescending doesn't work on AoT platforms, so we have to order the set manually..
			for (int j = 0; j < potentialPairs.Count - 1; j++)
			{
				for (int i = 0; i < potentialPairs.Count - 1; i++)
				{
					if (potentialPairs[i].TileWeight == potentialPairs[i + 1].TileWeight && potentialPairs[i].DoorwayWeight < potentialPairs[i + 1].DoorwayWeight)
					{
						var temp = potentialPairs[i];

						potentialPairs[i] = potentialPairs[i + 1];
						potentialPairs[i + 1] = temp;
					}
				}
			}

			return potentialPairs.Take(count);
		}

		private List<GameObjectChance> CalculateOrderedListOfTiles()
		{
			List<GameObjectChance> tiles = new List<GameObjectChance>(TileWeights.Count);

			GameObjectChanceTable table = new GameObjectChanceTable();
			table.Weights.AddRange(TileWeights);

			while (table.Weights.Any(x => x.Value != null && x.GetWeight(IsOnMainPath, NormalizedDepth) > 0.0f))
				tiles.Add(table.GetRandom(RandomStream, IsOnMainPath, NormalizedDepth, null, true, true));

			return tiles;
		}

		private IEnumerable<DoorwayPair> GetPotentialDoorwayPairsForNonFirstTile()
		{
			foreach(var previousDoor in PreviousTile.Placement.UnusedDoorways)
			{
				bool requiresSpecificExit = PreviousTile.Exit != null && !PreviousTile.Placement.UsedDoorways.Contains(PreviousTile.Exit);

				// If the previous tile must use a specific exit and this door isn't it, skip it
				if (requiresSpecificExit && PreviousTile.Exit != previousDoor)
					continue;

				foreach(var tileWeight in TileWeights)
				{
					// This tile wasn't even considered a possibility in the tile ordering phase, skip it
					if (!tileOrder.Contains(tileWeight))
						continue;

					var nextTile = GetTileTemplateDelegate(tileWeight.Value);
					float weight = tileOrder.Count - tileOrder.IndexOf(tileWeight);

					var nextTileComponent = tileWeight.Value.GetComponent<Tile>();

					if (IsTileAllowedPredicate != null && !IsTileAllowedPredicate(PreviousTile, PreviousPrefab, nextTileComponent, tileWeight.Value, ref weight))
						continue;

					foreach (var nextDoor in nextTile.Doorways)
					{
						bool requiresSpecificEntrance = nextTile.Tile != null && nextTile.Tile.Entrance != null;

						// If the next tile must use a specific entrance and this door isn't it, skip it
						if (requiresSpecificEntrance && nextTile.Tile.Entrance != nextDoor)
							continue;

						float doorwayWeight = 0f;

						if (IsValidDoorwayPairing(previousDoor, nextDoor, nextTile.Tile, ref doorwayWeight))
							yield return new DoorwayPair(PreviousTile, previousDoor, nextTile, nextDoor, tileWeight.TileSet, weight, doorwayWeight);
					}
				}
			}
		}

		private IEnumerable<DoorwayPair> GetPotentialDoorwayPairsForFirstTile()
		{
			foreach (var tileWeight in TileWeights)
			{
				// This tile wasn't even considered a possibility in the tile ordering phase, skip it
				if (!tileOrder.Contains(tileWeight))
					continue;

				var nextTile = GetTileTemplateDelegate(tileWeight.Value);
				float weight = tileWeight.GetWeight(IsOnMainPath, NormalizedDepth) * (float)RandomStream.NextDouble();

				var nextTileComponent = tileWeight.Value.GetComponent<Tile>();

				if (IsTileAllowedPredicate != null && !IsTileAllowedPredicate(PreviousTile, PreviousPrefab, nextTileComponent, tileWeight.Value, ref weight))
					continue;

				foreach (var nextDoor in nextTile.Doorways)
				{
					float doorwayWeight = CalculateDoorwayWeight(nextDoor);

					yield return new DoorwayPair(null, null, nextTile, nextDoor, tileWeight.TileSet, weight, doorwayWeight);
				}
			}
		}

		private bool IsValidDoorwayPairing(Doorway a, Doorway b, Tile nextTile, ref float weight)
		{
			// Enforce matching socket group
			if (!DoorwaySocket.IsMatchingSocket(a.SocketGroup, b.SocketGroup))
				return false;

			// Enforce facing-direction
			Vector3? forcedDirection = null;

			// If AllowRotation has been set to false, or if the tile to be placed disallows rotation, we must force a connection from the correct direction
			bool disallowRotation = (AllowRotation.HasValue && !AllowRotation.Value) || (nextTile != null && !nextTile.AllowRotation);

			// Always enforce facing direction for vertical doorways
			const float angleEpsilon = 1.0f;
			if (Vector3.Angle(a.transform.forward, UpVector) < angleEpsilon)
				forcedDirection = -UpVector;
			else if (Vector3.Angle(a.transform.forward, -UpVector) < angleEpsilon)
				forcedDirection = UpVector;
			else if (disallowRotation)
				forcedDirection = -a.transform.forward;

			if (forcedDirection.HasValue)
			{
				float angleDiff = Vector3.Angle(forcedDirection.Value, b.transform.forward);
				const float maxAngleDiff = 1.0f;

				if (angleDiff > maxAngleDiff)
					return false;
			}

			weight = CalculateDoorwayWeight(b);
			return true;
		}

		private float CalculateDoorwayWeight(Doorway doorway)
		{
			// Assign a random weight initially
			float weight = (float)RandomStream.NextDouble();

			float straightenChance = (Archetype == null) ? 0.0f : Archetype.StraightenChance;
			bool shouldTryStraighten =	straightenChance > 0 && IsOnMainPath &&
										PreviousTile.Placement.UsedDoorways.Count == 1 &&
										PreviousTile.Placement.UsedDoorways[0].transform.forward == -doorway.transform.forward;

			if (shouldTryStraighten)
			{
				double rnd = RandomStream.NextDouble();

				// Heavily scew weight in favour of picking doorways that would continue the dungeon in a straight line
				if (rnd < straightenChance)
					weight *= 100;
			}

			return weight;
		}
	}
}
