// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Profiling;
using UnityEngine.Pool;

namespace UnityEngine.UIElements
{
    internal abstract class TreeViewController : CollectionViewController
    {
        Dictionary<int, TreeItem> m_TreeItems = new Dictionary<int, TreeItem>();
        List<int> m_RootIndices = new List<int>();
        List<TreeViewItemWrapper> m_ItemWrappers = new List<TreeViewItemWrapper>();
        HashSet<int> m_TreeItemIdsWithItemWrappers = new HashSet<int>();
        List<TreeViewItemWrapper> m_WrapperInsertionList = new List<TreeViewItemWrapper>();

        protected Experimental.TreeView treeView => view as Experimental.TreeView;

        public void RebuildTree()
        {
            m_TreeItems.Clear();
            m_RootIndices.Clear();

            foreach (var itemId in GetAllItemIds())
            {
                var parentId = GetParentId(itemId);
                if (parentId == TreeItem.invalidId)
                    m_RootIndices.Add(itemId);

                m_TreeItems.Add(itemId, new TreeItem(itemId, parentId, GetChildrenIds(itemId)));
            }

            RegenerateWrappers();
        }

        public IEnumerable<int> GetRootItemIds()
        {
            return m_RootIndices;
        }

        public abstract IEnumerable<int> GetAllItemIds(IEnumerable<int> rootIds = null);
        public abstract int GetParentId(int id);
        public abstract IEnumerable<int> GetChildrenIds(int id);
        public abstract void Move(int id, int newParentId, int childIndex = -1, bool rebuildTree = true);
        public abstract bool TryRemoveItem(int id, bool rebuildTree = true);

        internal override void InvokeMakeItem(ReusableCollectionItem reusableItem)
        {
            if (reusableItem is ReusableTreeViewItem treeItem)
            {
                treeItem.Init(MakeItem());
                treeItem.onPointerUp += OnItemPointerUp;
                treeItem.onToggleValueChanged += ToggleExpandedState;
                if (treeView.autoExpand)
                {
                    treeView.expandedItemIds.Remove(treeItem.id);
                    treeView.schedule.Execute(() => ExpandItem(treeItem.id, true));
                }
            }
        }

        internal override void InvokeBindItem(ReusableCollectionItem reusableItem, int index)
        {
            if (reusableItem is ReusableTreeViewItem treeItem)
            {
                treeItem.Indent(GetIndentationDepthByIndex(index));
                treeItem.SetExpandedWithoutNotify(IsExpandedByIndex(index));
                treeItem.SetToggleVisibility(HasChildrenByIndex(index));
            }

            base.InvokeBindItem(reusableItem, index);
        }

        internal override void InvokeDestroyItem(ReusableCollectionItem reusableItem)
        {
            if (reusableItem is ReusableTreeViewItem treeItem)
            {
                treeItem.onPointerUp -= OnItemPointerUp;
                treeItem.onToggleValueChanged -= ToggleExpandedState;
            }

            base.InvokeDestroyItem(reusableItem);
        }

        /// <inheritdoc />
        protected override void BindItem(VisualElement element, int index)
        {
            if (treeView.bindItem == null)
            {
                var isMakeItemSet = treeView.makeItem != null;

                if (isMakeItemSet)
                    throw new NotImplementedException("You must specify bindItem if makeItem is specified.");

                var label = (Label)element;
                var item = GetItemForIndex(index);
                label.text = item?.ToString() ?? "null";
                return;
            }

            treeView.bindItem.Invoke(element, index);
        }

        private void OnItemPointerUp(PointerUpEvent evt)
        {
            if ((evt.modifiers & EventModifiers.Alt) == 0)
                return;

            var target = evt.currentTarget as VisualElement;
            var toggle = target.Q<Toggle>(Experimental.TreeView.itemToggleUssClassName);
            var index = ((ReusableTreeViewItem)toggle.userData).index;
            var id = GetIdForIndex(index);
            var wasExpanded = IsExpandedByIndex(index);

            if (!HasChildrenByIndex(index))
                return;

            var hashSet = new HashSet<int>(treeView.expandedItemIds);

            if (wasExpanded)
                hashSet.Remove(id);
            else
                hashSet.Add(id);

            var childrenIds = GetChildrenIdsByIndex(index);
            foreach (var childId in GetAllItemIds(childrenIds))
            {
                if (HasChildren(childId))
                {
                    if (wasExpanded)
                        hashSet.Remove(childId);
                    else
                        hashSet.Add(childId);
                }
            }

            treeView.expandedItemIds = hashSet.ToList();

            RegenerateWrappers();
            treeView.RefreshItems();

            evt.StopPropagation();
        }

        private void ToggleExpandedState(ChangeEvent<bool> evt)
        {
            var toggle = evt.target as Toggle;
            var index = ((ReusableTreeViewItem)toggle.userData).index;
            var isExpanded = IsExpandedByIndex(index);

            if (isExpanded)
                CollapseItemByIndex(index, false);
            else
                ExpandItemByIndex(index, false);

            // To make sure our TreeView gets focus, we need to force this. :(
            treeView.scrollView.contentContainer.Focus();
        }

        public override int GetItemsCount()
        {
            return m_ItemWrappers?.Count ?? 0;
        }

        public virtual int GetTreeCount()
        {
            return m_TreeItems.Count;
        }

        public override int GetIndexForId(int id)
        {
            if (m_TreeItemIdsWithItemWrappers.Contains(id))
            {
                for (var index = 0; index < m_ItemWrappers.Count; index++)
                {
                    var wrapper = m_ItemWrappers[index];
                    if (wrapper.id == id)
                    {
                        return index;
                    }
                }
            }

            return -1;
        }

        public override int GetIdForIndex(int index)
        {
            return IsIndexValid(index) ? m_ItemWrappers[index].id : TreeItem.invalidId;
        }

        public bool Exists(int id)
        {
            return m_TreeItems.ContainsKey(id);
        }

        public virtual bool HasChildren(int id)
        {
            if (m_TreeItems.TryGetValue(id, out var item))
                return item.hasChildren;

            return false;
        }

        public bool HasChildrenByIndex(int index)
        {
            return IsIndexValid(index) && m_ItemWrappers[index].hasChildren;
        }

        public IEnumerable<int> GetChildrenIdsByIndex(int index)
        {
            return IsIndexValid(index) ? m_ItemWrappers[index].childrenIds : null;
        }

        public int GetChildIndexForId(int id)
        {
            if (!m_TreeItems.TryGetValue(id, out var item))
                return -1;

            var index = 0;
            var itemIds = m_TreeItems.TryGetValue(item.parentId, out var parentItem) ? parentItem.childrenIds : m_RootIndices;
            foreach (var childId in itemIds)
            {
                if (childId == id)
                    return index;

                index++;
            }

            return -1;
        }

        public int GetIndentationDepth(int id)
        {
            var depth = 0;
            var parentId = GetParentId(id);
            while (parentId != -1)
            {
                parentId = GetParentId(parentId);
                depth++;
            }

            return depth;
        }

        public int GetIndentationDepthByIndex(int index)
        {
            var id = GetIdForIndex(index);
            return GetIndentationDepth(id);
        }

        public bool IsExpanded(int id)
        {
            return treeView.expandedItemIds.Contains(id);
        }

        public bool IsExpandedByIndex(int index)
        {
            if (!IsIndexValid(index))
                return false;

            return IsExpanded(m_ItemWrappers[index].id);
        }

        static readonly ProfilerMarker K_ExpandItemByIndex = new ProfilerMarker(ProfilerCategory.Scripts, "BaseTreeViewController.ExpandItemByIndex");

        public void ExpandItemByIndex(int index, bool expandAllChildren, bool refresh = true)
        {
            using var marker = K_ExpandItemByIndex.Auto();
            if (!HasChildrenByIndex(index))
                return;

            var id = GetIdForIndex(index);

            if (!treeView.expandedItemIds.Contains(id) || expandAllChildren)
            {
                var childrenIds = GetChildrenIdsByIndex(index);
                var childrenIdsList = new List<int>();
                foreach (var childId in childrenIds)
                {
                    if (!m_TreeItemIdsWithItemWrappers.Contains(childId))
                        childrenIdsList.Add(childId);
                }

                CreateWrappers(childrenIdsList, GetIndentationDepth(id) + 1, ref m_WrapperInsertionList);
                m_ItemWrappers.InsertRange(index + 1, m_WrapperInsertionList);
                if (!treeView.expandedItemIds.Contains(m_ItemWrappers[index].id))
                    treeView.expandedItemIds.Add(m_ItemWrappers[index].id);
                m_WrapperInsertionList.Clear();
            }

            if (expandAllChildren)
            {
                var childrenIds = GetChildrenIds(id);
                foreach (var childId in GetAllItemIds(childrenIds))
                    if (!treeView.expandedItemIds.Contains(childId))
                        ExpandItemByIndex(GetIndexForId(childId), true, false);
            }

            if (refresh)
                treeView.RefreshItems();
        }

        public void ExpandItem(int id, bool expandAllChildren, bool refresh = true)
        {
            if (!HasChildren(id))
                return;

            // Try to find it in the currently visible list.
            for (var i = 0; i < m_ItemWrappers.Count; ++i)
                if (m_ItemWrappers[i].id == id)
                    if (expandAllChildren || !IsExpandedByIndex(i))
                    {
                        ExpandItemByIndex(i, expandAllChildren, refresh);
                        return;
                    }

            if (treeView.expandedItemIds.Contains(id))
                return;

            treeView.expandedItemIds.Add(id);
        }

        public void CollapseItemByIndex(int index, bool collapseAllChildren)
        {
            if (!HasChildrenByIndex(index))
                return;

            if (collapseAllChildren)
            {
                var id = GetIdForIndex(index);
                var childrenIds = GetChildrenIds(id);
                foreach (var childId in GetAllItemIds(childrenIds))
                    treeView.expandedItemIds.Remove(childId);
            }

            treeView.expandedItemIds.Remove(GetIdForIndex(index));

            var recursiveChildCount = 0;
            var currentIndex = index + 1;
            var currentDepth = GetIndentationDepthByIndex(index);
            while (currentIndex < m_ItemWrappers.Count && GetIndentationDepthByIndex(currentIndex) > currentDepth)
            {
                recursiveChildCount++;
                currentIndex++;
            }
            var end = index + 1 + recursiveChildCount;
            for (int i = index + 1; i < end; i++)
            {
                m_TreeItemIdsWithItemWrappers.Remove(m_ItemWrappers[i].id);
            }

            m_ItemWrappers.RemoveRange(index + 1, recursiveChildCount);

            treeView.RefreshItems();
        }

        public void CollapseItem(int id, bool collapseAllChildren)
        {
            // Try to find it in the currently visible list.
            for (var i = 0; i < m_ItemWrappers.Count; ++i)
                if (m_ItemWrappers[i].id == id)
                    if (IsExpandedByIndex(i))
                    {
                        CollapseItemByIndex(i, collapseAllChildren);
                        return;
                    }

            if (!treeView.expandedItemIds.Contains(id))
                return;

            treeView.expandedItemIds.Remove(id);
        }

        public void ExpandAll()
        {
            foreach (var itemId in GetAllItemIds())
                if (!treeView.expandedItemIds.Contains(itemId))
                    treeView.expandedItemIds.Add(itemId);

            RegenerateWrappers();
            treeView.RefreshItems();
        }

        public void CollapseAll()
        {
            if (treeView.expandedItemIds.Count == 0)
                return;

            treeView.expandedItemIds.Clear();
            RegenerateWrappers();
            treeView.RefreshItems();
        }

        internal void RegenerateWrappers()
        {
            m_ItemWrappers.Clear();
            m_TreeItemIdsWithItemWrappers.Clear();

            var rootItemIds = GetRootItemIds();
            if (rootItemIds == null)
                return;

            CreateWrappers(rootItemIds, 0, ref m_ItemWrappers);
            SetItemsSourceWithoutNotify(m_ItemWrappers);
        }

        static readonly ProfilerMarker k_CreateWrappers = new ProfilerMarker("BaseTreeViewController.CreateWrappers");
        void CreateWrappers(IEnumerable<int> treeViewItemIds, int depth, ref List<TreeViewItemWrapper> wrappers)
        {
            using var marker = k_CreateWrappers.Auto();
            if (treeViewItemIds == null || wrappers == null || m_TreeItemIdsWithItemWrappers == null)
                return;

            foreach (var id in treeViewItemIds)
            {
                if (!m_TreeItems.TryGetValue(id, out var treeItem))
                    continue;

                var wrapper = new TreeViewItemWrapper(treeItem, depth);
                wrappers.Add(wrapper);
                m_TreeItemIdsWithItemWrappers.Add(id); 

                if (treeView?.expandedItemIds == null)
                    continue;

                if (treeView.expandedItemIds.Contains(wrapper.id) && wrapper.hasChildren)
                    CreateWrappers(GetChildrenIds(wrapper.id), depth + 1, ref wrappers);
            }
        }

        bool IsIndexValid(int index)
        {
            return index >= 0 && index < m_ItemWrappers.Count;
        }

        internal void RaiseItemParentChanged(int id, int newParentId)
        {
            RaiseItemIndexChanged(id, newParentId);
        }
    }

    internal sealed class DefaultTreeViewController<T> : TreeViewController
    {
        TreeData<T> m_TreeData;

        Stack<IEnumerator<int>> m_IteratorStack = new Stack<IEnumerator<int>>();

        public void SetRootItems(IList<TreeViewItemData<T>> items)
        {
            m_TreeData = new TreeData<T>(items);
            RebuildTree();
        }

        public void AddItem(in TreeViewItemData<T> item, int parentId, int childIndex, bool rebuildTree = true)
        {
            m_TreeData.AddItem(item, parentId, childIndex);

            if (rebuildTree)
                RebuildTree();
        }

        public override bool TryRemoveItem(int id, bool rebuildTree = true)
        {
            if (m_TreeData.TryRemove(id))
            {
                if (rebuildTree)
                    RebuildTree();

                return true;
            }

            return false;
        }

        public T GetDataForId(int id)
        {
            return m_TreeData.GetDataForId(id).data;
        }

        public T GetDataForIndex(int index)
        {
            var itemId = GetIdForIndex(index);
            return GetDataForId(itemId);
        }

        public override object GetItemForIndex(int index)
        {
            return GetDataForIndex(index);
        }

        public override int GetParentId(int id)
        {
            return m_TreeData.GetParentId(id);
        }

        public override bool HasChildren(int id)
        {
            return m_TreeData.GetDataForId(id).hasChildren;
        }

        static IEnumerable<int> GetItemIds(IEnumerable<TreeViewItemData<T>> items)
        {
            if (items == null)
                yield break;

            foreach (var item in items)
                yield return item.id;
        }

        public override IEnumerable<int> GetChildrenIds(int id)
        {
            var item = m_TreeData.GetDataForId(id);
            return GetItemIds(item.children);
        }

        public override void Move(int id, int newParentId, int childIndex = -1, bool rebuildTree = true)
        {
            if (id == newParentId)
                return;

            if (IsChildOf(newParentId, id))
                return;

            m_TreeData.Move(id, newParentId, childIndex);

            if (rebuildTree)
            {
                RebuildTree();
                RaiseItemParentChanged(id, newParentId);
            }
        }

        bool IsChildOf(int childId, int id)
        {
            var data = m_TreeData.GetDataForId(id);
            if (data.HasChildRecursive(childId))
                return true;

            return false;
        }

        public override IEnumerable<int> GetAllItemIds(IEnumerable<int> rootIds = null)
        {
            if (rootIds == null)
            {
                if (m_TreeData.rootItemIds == null)
                    yield break;

                rootIds = m_TreeData.rootItemIds;
            }

            var currentIterator = rootIds.GetEnumerator();

            while (true)
            {
                var hasNext = currentIterator.MoveNext();
                if (!hasNext)
                {
                    if (m_IteratorStack.Count > 0)
                    {
                        currentIterator = m_IteratorStack.Pop();
                        continue;
                    }

                    // We're at the end of the root items list.
                    break;
                }

                var currentItemId = currentIterator.Current;
                yield return currentItemId;

                if (HasChildren(currentItemId))
                {
                    m_IteratorStack.Push(currentIterator);
                    currentIterator = GetChildrenIds(currentItemId).GetEnumerator();
                }
            }
        }
    }
}
