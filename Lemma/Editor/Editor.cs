﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

using Lemma.Util;
using Lemma.Factories;
using ComponentBind;

namespace Lemma.Components
{
	public interface IEditorUIComponent : IComponent
	{
		void AddEditorElements(UIComponent propertyList, EditorUI ui);
	}

	public class Editor : Component<Main>, IUpdateableComponent
	{
		static Editor()
		{
			Factory<Main>.DefaultEditorComponents = delegate(Factory<Main> factory, Entity entity, Main main)
			{
				Transform transform = entity.Get<Transform>();
				if (transform == null)
					return;

				Model model = new Model();
				model.Filename.Value = "Models\\sphere";
				model.Color.Value = new Vector3(factory.Color.X, factory.Color.Y, factory.Color.Z);
				model.IsInstanced.Value = false;
				model.Scale.Value = new Vector3(0.5f);
				model.Editable = false;
				model.Serialize = false;

				entity.Add("EditorModel", model);

				model.Add(new Binding<Matrix, Vector3>(model.Transform, x => Matrix.CreateTranslation(x), transform.Position));
			};
		}

		public Property<PlayerIndex> PlayerIndex = new Property<PlayerIndex> { Editable = false };
		public Property<Vector3> Position = new Property<Vector3> { Editable = false };
		public Property<Matrix> Orientation = new Property<Matrix> { Editable = false };
		public Property<bool> MovementEnabled = new Property<bool> { Editable = false };
		public ListProperty<Entity> SelectedEntities = new ListProperty<Entity>();
		public Property<Transform> SelectedTransform = new Property<Transform> { Editable = false };
		public Property<string> Brush = new Property<string> { Editable = true };
		public Property<Map.Coordinate> Jitter = new Property<Map.Coordinate> { Editable = true };
		public Property<Map.Coordinate> JitterOctave = new Property<Map.Coordinate> { Value = new Map.Coordinate { X = 1, Y = 1, Z = 1 }, Editable = true };
		public Property<float> JitterOctaveMultiplier = new Property<float> { Value = 10.0f, Editable = true };
		public Property<int> BrushSize = new Property<int> { Editable = true };
		public Property<string> MapFile = new Property<string> { Editable = true };
		public Property<bool> NeedsSave = new Property<bool> { Editable = false };

		// Input properties
		public Property<bool> MapEditMode = new Property<bool> { Editable = false };
		public Property<Vector2> Movement = new Property<Vector2> { Editable = false };
		public Property<Vector2> Mouse = new Property<Vector2> { Editable = false };
		public Property<bool> Up = new Property<bool> { Editable = false };
		public Property<bool> Down = new Property<bool> { Editable = false };
		public Property<bool> SpeedMode = new Property<bool> { Editable = false };
		public Property<bool> Extend = new Property<bool> { Editable = false };
		public Property<bool> Empty = new Property<bool> { Editable = false };
		public Property<bool> Fill = new Property<bool> { Editable = false };
		public Property<bool> EditSelection = new Property<bool> { Editable = false };
		public Property<Map.Coordinate> VoxelSelectionStart = new Property<Map.Coordinate> { Editable = false };
		public Property<Map.Coordinate> VoxelSelectionEnd = new Property<Map.Coordinate> { Editable = false };
		public Property<bool> VoxelSelectionActive = new Property<bool> { Editable = false };

		public Property<float> CameraDistance = new Property<float> { Value = 10.0f, Editable = false };

		public Command<string> Spawn = new Command<string>();
		public Command Save = new Command();
		public Command Duplicate = new Command();
		public Command DeleteSelected = new Command();

		public enum TransformModes { None, Translate, Rotate };
		public Property<TransformModes> TransformMode = new Property<TransformModes> { Value = TransformModes.None, Editable = false };
		public enum TransformAxes { All, X, Y, Z };
		public Property<TransformAxes> TransformAxis = new Property<TransformAxes> { Value = TransformAxes.All, Editable = false };
		protected Vector3 transformCenter;
		protected Vector2 originalTransformMouse;
		protected List<Matrix> offsetTransforms = new List<Matrix>();

		public Command VoxelDuplicate = new Command();
		public Command VoxelCopy = new Command();
		public Command VoxelPaste = new Command();
		public Command StartVoxelTranslation = new Command();
		public Command StartTranslation = new Command();
		public Command StartRotation = new Command();
		public Command CommitTransform = new Command();
		public Command RevertTransform = new Command();
		public Command PropagateMaterial = new Command();
		public Command IntersectMaterial = new Command();
		public Command PropagateMaterialAll = new Command();
		public Command PropagateMaterialBox = new Command();
		public Command SampleMaterial = new Command();
		public Command DeleteMaterial = new Command();
		public Command DeleteMaterialAll = new Command();

		private Map.Coordinate originalSelectionStart;
		private Map.Coordinate originalSelectionEnd;
		private Map.Coordinate originalSelectionCoord;
		private bool voxelDuplicate;

		private Map.MapState mapState;
		private Map.Coordinate selectionStart;
		private Map.Coordinate lastCoord;
		private Map.Coordinate coord;
		private ProceduralGenerator generator;
		private float movementInterval;

		public Property<Map.Coordinate> Coordinate = new Property<Map.Coordinate> { Editable = true }; // Readonly, for displaying to the UI

		private bool justCommitedOrRevertedVoxelOperation;

		public Editor()
		{
			this.BrushSize.Value = 1;
			this.MovementEnabled.Value = true;
			this.Orientation.Value = Matrix.Identity;
		}

		private void restoreMap(Map.Coordinate start, Map.Coordinate end, bool eraseOriginal, int offsetX = 0, int offsetY = 0, int offsetZ = 0)
		{
			Map map = this.SelectedEntities[0].Get<Map>();
			List<Map.Coordinate> removals = new List<Map.Coordinate>();
			for (int x = start.X; x < end.X; x++)
			{
				for (int y = start.Y; y < end.Y; y++)
				{
					for (int z = start.Z; z < end.Z; z++)
					{
						Map.CellState desiredState;
						if (eraseOriginal && x >= this.originalSelectionStart.X && x < this.originalSelectionEnd.X
							&& y >= this.originalSelectionStart.Y && y < this.originalSelectionEnd.Y
							&& z >= this.originalSelectionStart.Z && z < this.originalSelectionEnd.Z)
							desiredState = null;
						else
							desiredState = this.mapState[new Map.Coordinate { X = x + offsetX, Y = y + offsetY, Z = z + offsetZ }];
						if (map[x, y, z] != desiredState)
							removals.Add(new Map.Coordinate { X = x, Y = y, Z = z });
					}
				}
			}
			map.Empty(removals, true);

			for (int x = start.X; x < end.X; x++)
			{
				for (int y = start.Y; y < end.Y; y++)
				{
					for (int z = start.Z; z < end.Z; z++)
					{
						Map.CellState desiredState;
						if (eraseOriginal && x >= this.originalSelectionStart.X && x < this.originalSelectionEnd.X
							&& y >= this.originalSelectionStart.Y && y < this.originalSelectionEnd.Y
							&& z >= this.originalSelectionStart.Z && z < this.originalSelectionEnd.Z)
							desiredState = null;
						else
							desiredState = this.mapState[new Map.Coordinate { X = x + offsetX, Y = y + offsetY, Z = z + offsetZ }];
						if (desiredState != null && map[x, y, z] != desiredState)
							map.Fill(x, y, z, desiredState);
					}
				}
			}
			map.Regenerate();
		}

		private Map.CellState getBrush()
		{
			Map.CellState result = Map.StateList.FirstOrDefault(x => x.ID.ToString() == this.Brush);
			if (result.ID == Map.t.Empty)
				return Map.EmptyState;
			return result;
		}

		public override void Awake()
		{
			base.Awake();
			this.generator = this.Entity.GetOrCreate<ProceduralGenerator>();

			this.Spawn.Action = delegate(string type)
			{
				if (Factory<Main>.Get(type) != null)
				{
					Entity entity = Factory<Main>.Get(type).CreateAndBind(this.main);
					Transform position = entity.Get<Transform>();
					if (position != null)
						position.Position.Value = this.Position;
					this.NeedsSave.Value = true;
					this.main.Add(entity);
					this.SelectedEntities.Clear();
					this.SelectedEntities.Add(entity);
					this.StartTranslation.Execute();
				}
			};

			this.Save.Action = delegate()
			{
				IO.MapLoader.Save(this.main, null, this.main.MapFile);
				this.NeedsSave.Value = false;
			};

			this.Duplicate.Action = delegate()
			{
				this.NeedsSave.Value = true;

				if (this.TransformMode.Value != TransformModes.None)
					this.CommitTransform.Execute();

				IEnumerable<Entity> entities = this.SelectedEntities.ToList();
				this.SelectedEntities.Clear();
				foreach (Entity entity in entities)
				{
					Entity copy = Factory<Main>.Duplicate(this.main, entity);
					this.main.Add(copy);
					this.SelectedEntities.Add(copy);
				}
				this.StartTranslation.Execute();
			};

			this.Brush.Value = "(Procedural)";

			this.MapEditMode.Set = delegate(bool value)
			{
				bool oldValue = this.MapEditMode.InternalValue;
				this.MapEditMode.InternalValue = value;
				if (value && !oldValue)
				{
					this.Orientation.Value = this.SelectedEntities[0].Get<Transform>().Orientation;
					this.lastCoord = this.coord = this.SelectedEntities[0].Get<Map>().GetCoordinate(this.Position);
					this.Coordinate.Value = this.coord;
				}
				else if (!value && oldValue)
					this.Orientation.Value = Matrix.Identity;
			};

			this.SelectedEntities.ItemAdded += delegate(int index, Entity t)
			{
				Property<bool> selected = t.GetProperty<bool>("EditorSelected");
				if (selected != null)
					selected.Value = true;
				this.VoxelSelectionEnd.Value = this.VoxelSelectionStart.Value;
				this.SelectedTransform.Value = null;
			};

			this.SelectedEntities.ItemRemoved += delegate(int index, Entity t)
			{
				Property<bool> selected = t.GetProperty<bool>("EditorSelected");
				if (selected != null)
					selected.Value = false;
				this.VoxelSelectionEnd.Value = this.VoxelSelectionStart.Value;
				this.SelectedTransform.Value = null;
			};

			this.SelectedEntities.Clearing += delegate()
			{
				foreach (Entity e in this.SelectedEntities)
				{
					Property<bool> selected = e.GetProperty<bool>("EditorSelected");
					if (selected != null)
						selected.Value = false;
				}
				this.VoxelSelectionEnd.Value = this.VoxelSelectionStart.Value;
				this.SelectedTransform.Value = null;
			};

			this.EditSelection.Set = delegate(bool value)
			{
				if (value && !this.EditSelection.InternalValue)
				{
					this.selectionStart = this.coord;
					this.VoxelSelectionStart.Value = this.coord;
					this.VoxelSelectionEnd.Value = this.coord.Move(1, 1, 1);
				}
				else if (!value && this.EditSelection.InternalValue)
				{
					if (this.VoxelSelectionEnd.Value.Equivalent(this.VoxelSelectionStart.Value.Move(1, 1, 1)))
						this.VoxelSelectionEnd.Value = this.VoxelSelectionStart.Value;
				}
				this.EditSelection.InternalValue = value;
			};

			this.VoxelCopy.Action = delegate()
			{
				if (this.MapEditMode && this.VoxelSelectionActive)
				{
					Map m = this.SelectedEntities[0].Get<Map>();
					this.originalSelectionStart = this.VoxelSelectionStart;
					this.originalSelectionEnd = this.VoxelSelectionEnd;
					this.originalSelectionCoord = this.coord;
					if (this.mapState != null)
						this.mapState.Free();
					this.mapState = new Map.MapState(m, this.originalSelectionStart, this.originalSelectionEnd);
					this.voxelDuplicate = false;
				}
			};

			this.VoxelPaste.Action = delegate()
			{
				if (this.MapEditMode && this.mapState != null)
				{
					Map m = this.SelectedEntities[0].Get<Map>();
					Map.Coordinate newSelectionStart = this.coord.Plus(this.originalSelectionStart.Minus(this.originalSelectionCoord));
					this.VoxelSelectionStart.Value = newSelectionStart;
					this.VoxelSelectionEnd.Value = this.coord.Plus(this.originalSelectionEnd.Minus(this.originalSelectionCoord));

					this.mapState.Add(this.VoxelSelectionStart, this.VoxelSelectionEnd);

					Map.Coordinate offset = this.originalSelectionStart.Minus(newSelectionStart);
					this.restoreMap(newSelectionStart, this.VoxelSelectionEnd, false, offset.X, offset.Y, offset.Z);
				}
			};

			this.StartVoxelTranslation.Action = delegate()
			{
				if (this.MapEditMode && this.VoxelSelectionActive)
				{
					this.VoxelCopy.Execute();
					this.TransformMode.Value = TransformModes.Translate;
				}
			};

			this.VoxelDuplicate.Action = delegate()
			{
				if (this.MapEditMode && this.VoxelSelectionActive)
				{
					this.StartVoxelTranslation.Execute();
					this.voxelDuplicate = true;
				}
			};

			this.PropagateMaterial.Action = delegate()
			{
				if (!this.MapEditMode)
					return;

				Map m = this.SelectedEntities[0].Get<Map>();
				Map.Box selectedBox = m.GetBox(this.coord);
				if (selectedBox == null)
					return;

				Map.Coordinate startSelection = this.VoxelSelectionStart;
				Map.Coordinate endSelection = this.VoxelSelectionEnd;
				bool selectionActive = this.VoxelSelectionActive;

				Map.CellState material = this.getBrush();
				if (material != Map.EmptyState)
				{
					if (material == selectedBox.Type)
						return;

					IEnumerable<Map.Coordinate> coordEnumerable;
					if (selectionActive)
						coordEnumerable = m.GetContiguousByType(new Map.Box[] { selectedBox }).SelectMany(x => x.GetCoords().Where(y => y.Between(startSelection, endSelection)));
					else
						coordEnumerable = m.GetContiguousByType(new Map.Box[] { selectedBox }).SelectMany(x => x.GetCoords());

					List<Map.Coordinate> coords = coordEnumerable.ToList();
					m.Empty(coords, true);
					foreach (Map.Coordinate c in coords)
						m.Fill(c, material);
					m.Regenerate();
				}
			};

			this.IntersectMaterial.Action = delegate()
			{
				if (!this.MapEditMode)
					return;

				Map m = this.SelectedEntities[0].Get<Map>();
				Map.Box selectedBox = m.GetBox(this.coord);
				if (selectedBox == null)
					return;

				Map.Coordinate startSelection = this.VoxelSelectionStart;
				Map.Coordinate endSelection = this.VoxelSelectionEnd;
				bool selectionActive = this.VoxelSelectionActive;

				IEnumerable<Map.Coordinate> coordEnumerable;
				if (selectionActive)
					coordEnumerable = m.GetContiguousByType(new Map.Box[] { selectedBox }).SelectMany(x => x.GetCoords().Where(y => !y.Between(startSelection, endSelection)));
				else
					coordEnumerable = m.GetContiguousByType(new Map.Box[] { selectedBox }).SelectMany(x => x.GetCoords().Where(y => (m.GetRelativePosition(this.coord) - m.GetRelativePosition(y)).Length() > this.BrushSize));

				List<Map.Coordinate> coords = coordEnumerable.ToList();
				m.Empty(coords, true);
				m.Regenerate();
			};

			// Propagate to all cells of a certain type, including non-contiguous ones
			this.PropagateMaterialAll.Action = delegate()
			{
				if (!this.MapEditMode)
					return;

				Map m = this.SelectedEntities[0].Get<Map>();
				Map.Box selectedBox = m.GetBox(this.coord);
				if (selectedBox == null)
					return;

				Map.CellState oldMaterial = selectedBox.Type;

				Map.CellState material = this.getBrush();
				if (material != Map.EmptyState)
				{
					if (material == oldMaterial)
						return;
					List<Map.Coordinate> coords = m.Chunks.SelectMany(x => x.Boxes).Where(x => x.Type == oldMaterial).SelectMany(x => x.GetCoords()).ToList();
					m.Empty(coords, true);
					foreach (Map.Coordinate c in coords)
						m.Fill(c, material);
					m.Regenerate();
				}
			};

			this.PropagateMaterialBox.Action = delegate()
			{
				if (!this.MapEditMode)
					return;

				Map m = this.SelectedEntities[0].Get<Map>();
				Map.Box selectedBox = m.GetBox(this.coord);
				if (selectedBox == null)
					return;

				Map.Coordinate startSelection = this.VoxelSelectionStart;
				Map.Coordinate endSelection = this.VoxelSelectionEnd;
				bool selectionActive = this.VoxelSelectionActive;

				Map.CellState material = this.getBrush();
				if (material != Map.EmptyState)
				{
					if (material == selectedBox.Type)
						return;

					IEnumerable<Map.Coordinate> coordEnumerable;
					if (selectionActive)
						coordEnumerable = selectedBox.GetCoords().Where(y => y.Between(startSelection, endSelection));
					else
						coordEnumerable = selectedBox.GetCoords();

					List<Map.Coordinate> coords = coordEnumerable.ToList();
					m.Empty(coords, true);
					foreach (Map.Coordinate c in coords)
						m.Fill(c, material);
					m.Regenerate();
				}
			};

			this.SampleMaterial.Action = delegate()
			{
				if (!this.MapEditMode)
					return;

				Map m = this.SelectedEntities[0].Get<Map>();
				Map.Box selectedBox = m.GetBox(this.coord);
				if (selectedBox == null)
					return;

				this.Brush.Value = selectedBox.Type.ID.ToString();
			};

			this.DeleteMaterial.Action = delegate()
			{
				if (!this.MapEditMode)
					return;

				Map m = this.SelectedEntities[0].Get<Map>();
				Map.Box selectedBox = m.GetBox(this.coord);
				if (selectedBox == null)
					return;

				Map.Coordinate startSelection = this.VoxelSelectionStart;
				Map.Coordinate endSelection = this.VoxelSelectionEnd;
				bool selectionActive = this.VoxelSelectionActive;

				IEnumerable<Map.Coordinate> coordEnumerable;
				if (selectionActive)
					coordEnumerable = m.GetContiguousByType(new Map.Box[] { selectedBox }).SelectMany(x => x.GetCoords().Where(y => y.Between(startSelection, endSelection)));
				else
					coordEnumerable = m.GetContiguousByType(new Map.Box[] { selectedBox }).SelectMany(x => x.GetCoords());

				List<Map.Coordinate> coords = coordEnumerable.ToList();
				m.Empty(coords, true);
				m.Regenerate();
			};

			// Delete all cells of a certain type in the current map, including non-contiguous ones
			this.DeleteMaterialAll.Action = delegate()
			{
				if (!this.MapEditMode)
					return;

				Map m = this.SelectedEntities[0].Get<Map>();
				Map.Box selectedBox = m.GetBox(this.coord);
				if (selectedBox == null)
					return;

				Map.CellState material = selectedBox.Type;

				m.Empty(m.Chunks.SelectMany(x => x.Boxes).Where(x => x.Type == material).SelectMany(x => x.GetCoords()).ToList(), true);
				m.Regenerate();
			};

			Action<TransformModes> startTransform = delegate(TransformModes mode)
			{
				this.TransformMode.Value = mode;
				this.TransformAxis.Value = TransformAxes.All;
				this.originalTransformMouse = this.Mouse;
				this.offsetTransforms.Clear();
				this.transformCenter = Vector3.Zero;
				if (this.SelectedTransform.Value != null)
				{
					this.offsetTransforms.Add(this.SelectedTransform.Value.Matrix);
					this.transformCenter = this.SelectedTransform.Value.Position;
				}
				else
				{
					int entityCount = 0;
					foreach (Entity entity in this.SelectedEntities)
					{
						Transform transform = entity.Get<Transform>();
						if (transform != null)
						{
							this.offsetTransforms.Add(transform.Matrix);
							this.transformCenter += transform.Position;
							entityCount++;
						}
					}
					this.transformCenter /= (float)entityCount;
				}
			};

			this.StartTranslation.Action = delegate()
			{
				startTransform(TransformModes.Translate);
			};

			this.StartRotation.Action = delegate()
			{
				startTransform(TransformModes.Rotate);
			};

			this.CommitTransform.Action = delegate()
			{
				this.NeedsSave.Value = true;
				this.TransformMode.Value = TransformModes.None;
				this.TransformAxis.Value = TransformAxes.All;
				if (this.MapEditMode)
					this.justCommitedOrRevertedVoxelOperation = true;
				this.offsetTransforms.Clear();
			};

			this.RevertTransform.Action = delegate()
			{
				this.TransformMode.Value = TransformModes.None;
				if (this.MapEditMode)
				{
					this.restoreMap(this.VoxelSelectionStart, this.VoxelSelectionEnd, false);
					this.VoxelSelectionStart.Value = this.originalSelectionStart;
					this.VoxelSelectionEnd.Value = this.originalSelectionEnd;
					this.restoreMap(this.VoxelSelectionStart, this.VoxelSelectionEnd, false);
					this.justCommitedOrRevertedVoxelOperation = true;
				}
				else
				{
					this.TransformAxis.Value = TransformAxes.All;
					if (this.SelectedTransform.Value != null)
						this.SelectedTransform.Value.Matrix.Value = this.offsetTransforms[0];
					else
					{
						int i = 0;
						foreach (Entity entity in this.SelectedEntities)
						{
							Transform transform = entity.Get<Transform>();
							if (transform != null)
							{
								transform.Matrix.Value = this.offsetTransforms[i];
								i++;
							}
						}
					}
					this.offsetTransforms.Clear();
				}
			};

			this.DeleteSelected.Action = delegate()
			{
				this.NeedsSave.Value = true;
				this.TransformMode.Value = TransformModes.None;
				this.TransformAxis.Value = TransformAxes.All;
				this.offsetTransforms.Clear();
				foreach (Entity entity in this.SelectedEntities)
				{
					if (entity.EditorCanDelete)
						entity.Delete.Execute();
				}
				this.SelectedEntities.Clear();
			};

			this.Add(new Binding<bool>(this.VoxelSelectionActive, delegate()
			{
				if (!this.MapEditMode)
					return false;
				Map.Coordinate start = this.VoxelSelectionStart, end = this.VoxelSelectionEnd;
				return start.X != end.X && start.Y != end.Y && start.Z != end.Z;
			}, this.MapEditMode, this.VoxelSelectionStart, this.VoxelSelectionEnd));
		}

		public void Update(float elapsedTime)
		{
			Vector3 movementDir = new Vector3();
			if (this.MovementEnabled)
			{
				Vector2 controller = this.main.Camera.GetWorldSpaceControllerCoordinates(this.Movement);
				movementDir = new Vector3(controller.X, 0, controller.Y);
				if (this.Up)
					movementDir = movementDir.SetComponent(Direction.PositiveY, 1.0f);
				else if (this.Down)
					movementDir = movementDir.SetComponent(Direction.NegativeY, 1.0f);
					
				if (this.MapEditMode)
				{
					bool moving = movementDir.LengthSquared() > 0.0f;

					// When the user lets go of the key, reset the timer
					// That way they can hit the key faster than the 0.1 sec interval
					if (!moving)
						this.movementInterval = 0.5f; 

					if (this.movementInterval > (this.SpeedMode ? 0.5f : 1.0f) / this.CameraDistance)
					{
						if (moving)
							this.movementInterval = 0.0f;
						if (movementDir.LengthSquared() > 0.0f)
						{
							Map map = this.SelectedEntities[0].Get<Map>();
							Direction relativeDir = map.GetRelativeDirection(movementDir);
							this.coord = this.coord.Move(relativeDir);
							this.Coordinate.Value = this.coord;
							if (this.EditSelection)
							{
								this.VoxelSelectionStart.Value = new Map.Coordinate
								{
									X = Math.Min(this.selectionStart.X, this.coord.X),
									Y = Math.Min(this.selectionStart.Y, this.coord.Y),
									Z = Math.Min(this.selectionStart.Z, this.coord.Z),
								};
								this.VoxelSelectionEnd.Value = new Map.Coordinate
								{
									X = Math.Max(this.selectionStart.X, this.coord.X) + 1,
									Y = Math.Max(this.selectionStart.Y, this.coord.Y) + 1,
									Z = Math.Max(this.selectionStart.Z, this.coord.Z) + 1,
								};
							}
							else if (this.TransformMode.Value == TransformModes.Translate)
							{
								this.NeedsSave.Value = true;

								this.restoreMap(this.VoxelSelectionStart, this.VoxelSelectionEnd, !this.voxelDuplicate);

								Map.Coordinate newSelectionStart = this.VoxelSelectionStart.Value.Move(relativeDir);
								this.VoxelSelectionStart.Value = newSelectionStart;
								this.VoxelSelectionEnd.Value = this.VoxelSelectionEnd.Value.Move(relativeDir);

								this.mapState.Add(this.VoxelSelectionStart, this.VoxelSelectionEnd);

								Map.Coordinate offset = this.originalSelectionStart.Minus(newSelectionStart);
								this.restoreMap(newSelectionStart, this.VoxelSelectionEnd, false, offset.X, offset.Y, offset.Z);
							}
							this.Position.Value = map.GetAbsolutePosition(this.coord);
						}
					}
					this.movementInterval += elapsedTime;
				}
				else
					this.Position.Value = this.Position.Value + movementDir * (this.SpeedMode ? 5.0f : 2.5f) * this.CameraDistance * elapsedTime;
			}

			if (this.MapEditMode)
			{
				if (!this.Fill && !this.Empty)
					this.justCommitedOrRevertedVoxelOperation = false;

				Map map = this.SelectedEntities[0].Get<Map>();
				Map.Coordinate coord = map.GetCoordinate(this.Position);
				if (this.TransformMode.Value == TransformModes.None && (this.Fill || this.Empty || this.Extend) && !this.justCommitedOrRevertedVoxelOperation)
				{
					this.NeedsSave.Value = true;
					if (this.Brush == "(Procedural)")
					{
						if (this.Fill)
						{
							if (this.VoxelSelectionActive)
							{
								foreach (Map.Coordinate c in this.VoxelSelectionStart.Value.CoordinatesBetween(this.VoxelSelectionEnd))
									map.Fill(c, this.generator.GetValue(map, c));
							}
							else
								this.brushStroke(map, coord, this.BrushSize, x => this.generator.GetValue(map, x), true, false);
						}
						else if (this.Empty)
						{
							if (this.VoxelSelectionActive)
								map.Empty(this.VoxelSelectionStart.Value.CoordinatesBetween(this.VoxelSelectionEnd).Where(x => this.generator.GetValue(map, x).ID == 0), true);
							else
								this.brushStroke(map, coord, this.BrushSize, x => this.generator.GetValue(map, x), false, true);
						}
					}
					else
					{
						if (this.Fill)
						{
							Map.CellState material = this.getBrush();
							if (material != Map.EmptyState)
							{
								if (this.VoxelSelectionActive)
								{
									if (this.Jitter.Value.Equivalent(new Map.Coordinate { X = 0, Y = 0, Z = 0 }) || this.BrushSize <= 1)
										map.Fill(this.VoxelSelectionStart, this.VoxelSelectionEnd, material);
									else
									{
										Map.Coordinate start = this.VoxelSelectionStart;
										Map.Coordinate end = this.VoxelSelectionEnd;
										int size = this.BrushSize;
										int halfSize = size / 2;
										for (int x = start.X + size - 1; x < end.X - size + 1; x += halfSize)
										{
											for (int y = start.Y + size - 1; y < end.Y - size + 1; y += halfSize)
											{
												for (int z = start.Z + size - 1; z < end.Z - size + 1; z += halfSize)
												{
													this.brushStroke(map, new Map.Coordinate { X = x, Y = y, Z = z }, size, material);
												}
											}
										}
									}
								}
								else
									this.brushStroke(map, coord, this.BrushSize, material);
							}
						}
						else if (this.Empty)
						{
							if (this.VoxelSelectionActive)
							{
								if (this.Jitter.Value.Equivalent(new Map.Coordinate { X = 0, Y = 0, Z = 0 }) || this.BrushSize <= 1)
									map.Empty(this.VoxelSelectionStart, this.VoxelSelectionEnd, true);
								else
								{
									Map.Coordinate start = this.VoxelSelectionStart;
									Map.Coordinate end = this.VoxelSelectionEnd;
									int size = this.BrushSize;
									int halfSize = size / 2;
									for (int x = start.X + size - 2; x < end.X - size; x += halfSize)
									{
										for (int y = start.Y + size - 2; y < end.Y - size; y += halfSize)
										{
											for (int z = start.Z + size - 2; z < end.Z - size; z += halfSize)
											{
												this.brushStroke(map, new Map.Coordinate { X = x, Y = y, Z = z }, size, new Map.CellState());
											}
										}
									}
								}
							}
							else
								this.brushStroke(map, coord, this.BrushSize, new Map.CellState());
						}
					}

					if (this.Extend && !this.coord.Equivalent(this.lastCoord))
					{
						Direction dir = DirectionExtensions.GetDirectionFromVector(Vector3.TransformNormal(movementDir, Matrix.Invert(map.Transform)));
						Map.Box box = map.GetBox(this.lastCoord);
						bool grow = map.GetBox(this.coord) != box;
						if (box != null)
						{
							List<Map.Coordinate> removals = new List<Map.Coordinate>();
							if (dir.IsParallel(Direction.PositiveX))
							{
								for (int y = box.Y; y < box.Y + box.Height; y++)
								{
									for (int z = box.Z; z < box.Z + box.Depth; z++)
									{
										if (grow)
											map.Fill(this.coord.X, y, z, box.Type);
										else
											removals.Add(map.GetCoordinate(this.lastCoord.X, y, z));
									}
								}
							}
							if (dir.IsParallel(Direction.PositiveY))
							{
								for (int x = box.X; x < box.X + box.Width; x++)
								{
									for (int z = box.Z; z < box.Z + box.Depth; z++)
									{
										if (grow)
											map.Fill(x, this.coord.Y, z, box.Type);
										else
											removals.Add(map.GetCoordinate(x, this.lastCoord.Y, z));
									}
								}
							}
							if (dir.IsParallel(Direction.PositiveZ))
							{
								for (int x = box.X; x < box.X + box.Width; x++)
								{
									for (int y = box.Y; y < box.Y + box.Height; y++)
									{
										if (grow)
											map.Fill(x, y, this.coord.Z, box.Type);
										else
											removals.Add(map.GetCoordinate(x, y, this.lastCoord.Z));
									}
								}
							}
							map.Empty(removals, true);
						}
					}
					map.Regenerate();
				}
				this.lastCoord = this.coord;
			}
			else if (this.TransformMode.Value == TransformModes.Translate)
			{
				// Translate entities
				this.NeedsSave.Value = true;
				float rayLength = (this.transformCenter - this.main.Camera.Position.Value).Length();
				Vector2 mouseOffset = this.Mouse - this.originalTransformMouse;
				Vector3 offset = ((this.main.Camera.Right.Value * mouseOffset.X * rayLength) + (this.main.Camera.Up.Value * -mouseOffset.Y * rayLength)) * 0.0025f;
				switch (this.TransformAxis.Value)
				{
					case TransformAxes.X:
						offset.Y = offset.Z = 0.0f;
						break;
					case TransformAxes.Y:
						offset.X = offset.Z = 0.0f;
						break;
					case TransformAxes.Z:
						offset.X = offset.Y = 0.0f;
						break;
				}
				if (this.SelectedTransform.Value != null)
					this.SelectedTransform.Value.Position.Value = this.offsetTransforms[0].Translation + offset;
				else
				{
					int i = 0;
					foreach (Entity entity in this.SelectedEntities)
					{
						Transform transform = entity.Get<Transform>();
						if (transform != null)
						{
							Matrix originalTransform = this.offsetTransforms[i];
							transform.Position.Value = originalTransform.Translation + offset;
							i++;
						}
					}
				}
			}
			else if (this.TransformMode.Value == TransformModes.Rotate)
			{
				// Rotate entities
				this.NeedsSave.Value = true;
				Vector3 screenSpaceCenter = this.main.GraphicsDevice.Viewport.Project(this.transformCenter, this.main.Camera.Projection, this.main.Camera.View, Matrix.Identity);
				Vector2 originalOffset = new Vector2(this.originalTransformMouse.X - screenSpaceCenter.X, this.originalTransformMouse.Y - screenSpaceCenter.Y);
				float originalAngle = (float)Math.Atan2(originalOffset.Y, originalOffset.X);
				Vector2 newOffset = new Vector2(this.Mouse.Value.X - screenSpaceCenter.X, this.Mouse.Value.Y - screenSpaceCenter.Y);
				float newAngle = (float)Math.Atan2(newOffset.Y, newOffset.X);
				Vector3 axis = this.main.Camera.Forward;
				switch (this.TransformAxis.Value)
				{
					case TransformAxes.X:
						axis = Vector3.Right;
						break;
					case TransformAxes.Y:
						axis = Vector3.Up;
						break;
					case TransformAxes.Z:
						axis = Vector3.Forward;
						break;
				}
				if (this.SelectedTransform.Value != null)
				{
					Matrix originalTransform = this.offsetTransforms[0];
					originalTransform.Translation -= this.transformCenter;
					originalTransform *= Matrix.CreateFromAxisAngle(axis, newAngle - originalAngle);
					originalTransform.Translation += this.transformCenter;
					this.SelectedTransform.Value.Matrix.Value = originalTransform;
				}
				else
				{
					int i = 0;
					foreach (Entity entity in this.SelectedEntities)
					{
						Transform transform = entity.Get<Transform>();
						if (transform != null)
						{
							Matrix originalTransform = this.offsetTransforms[i];
							originalTransform.Translation -= this.transformCenter;
							originalTransform *= Matrix.CreateFromAxisAngle(axis, newAngle - originalAngle);
							originalTransform.Translation += this.transformCenter;
							transform.Matrix.Value = originalTransform;
							i++;
						}
					}
				}
			}
		}

		protected Map.Coordinate jitter(Map map, Map.Coordinate coord)
		{
			Map.Coordinate octave = this.JitterOctave;
			Map.Coordinate jitter = this.Jitter;
			Map.Coordinate sample = coord.Clone();
			sample.X *= octave.X;
			sample.Y *= octave.Y;
			sample.Z *= octave.Z;
			coord.X += (int)Math.Round(this.generator.Sample(map, sample.Move(0, 0, map.ChunkSize * 2), this.JitterOctaveMultiplier) * jitter.X);
			coord.Y += (int)Math.Round(this.generator.Sample(map, sample.Move(map.ChunkSize * 2, 0, 0), this.JitterOctaveMultiplier) * jitter.Y);
			coord.Z += (int)Math.Round(this.generator.Sample(map, sample.Move(0, map.ChunkSize * 2, 0), this.JitterOctaveMultiplier) * jitter.Z);
			return coord;
		}

		protected void brushStroke(Map map, Map.Coordinate center, int brushSize, Func<Map.Coordinate, Map.CellState> function, bool fill = true, bool empty = true)
		{
			if (brushSize > 1)
				center = this.jitter(map, center);

			Vector3 pos = map.GetRelativePosition(center);
			List<Map.Coordinate> coords = new List<Map.Coordinate>();
			for (Map.Coordinate x = center.Move(Direction.NegativeX, this.BrushSize - 1); x.X < center.X + this.BrushSize; x.X++)
			{
				for (Map.Coordinate y = x.Move(Direction.NegativeY, this.BrushSize - 1); y.Y < center.Y + this.BrushSize; y.Y++)
				{
					for (Map.Coordinate z = y.Move(Direction.NegativeZ, this.BrushSize - 1); z.Z < center.Z + this.BrushSize; z.Z++)
					{
						if ((pos - map.GetRelativePosition(z)).Length() <= this.BrushSize)
							coords.Add(new Map.Coordinate { X = z.X, Y = z.Y, Z = z.Z, Data = function(z) });
					}
				}
			}

			if (empty)
				map.Empty(coords.Where(x => x.Data.ID == 0), true);

			if (fill)
			{
				foreach (Map.Coordinate coord in coords)
					map.Fill(coord, coord.Data);
			}
		}

		protected void brushStroke(Map map, Map.Coordinate center, int brushSize, Map.CellState state)
		{
			if (brushSize > 1)
				center = this.jitter(map, center);

			Vector3 pos = map.GetRelativePosition(center);
			List<Map.Coordinate> coords = new List<Map.Coordinate>();
			for (Map.Coordinate x = center.Move(Direction.NegativeX, this.BrushSize - 1); x.X < center.X + this.BrushSize; x.X++)
			{
				for (Map.Coordinate y = x.Move(Direction.NegativeY, this.BrushSize - 1); y.Y < center.Y + this.BrushSize; y.Y++)
				{
					for (Map.Coordinate z = y.Move(Direction.NegativeZ, this.BrushSize - 1); z.Z < center.Z + this.BrushSize; z.Z++)
					{
						if ((pos - map.GetRelativePosition(z)).Length() <= this.BrushSize)
							coords.Add(z);
					}
				}
			}
			if (state.ID == 0)
				map.Empty(coords, true);
			else
			{
				foreach (Map.Coordinate coord in coords)
					map.Fill(coord, state);
			}
		}
	}
}
