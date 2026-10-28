package vfscache

import (
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/rclone/rclone/fs"
	"github.com/rclone/rclone/fs/config"
	"github.com/rclone/rclone/lib/ranges"
	"github.com/rclone/rclone/vfs/vfscommon"
	"github.com/stretchr/testify/require"
)

func TestCloudSSDPreservesSparseDirtySteamDelete(t *testing.T) {
	oldCache := config.GetCacheDir()
	require.NoError(t, config.SetCacheDir(t.TempDir()))
	t.Cleanup(func() { require.NoError(t, config.SetCacheDir(oldCache)) })
	opt := vfscommon.Opt
	opt.CachePollInterval = 0
	opt.HandleCaching = 0
	opt.WriteBack = fs.Duration(time.Hour)
	_, c := newTestCacheOpt(t, opt)
	name := "SteamLibrary/steamapps/downloading/3240220/x64a.rpf"
	item := c.Item(name)
	require.NoError(t, item.Open(nil))
	require.NoError(t, item.Truncate(16*1024*1024))
	_, err := item.WriteAt([]byte("PATCH"), 1024*1024)
	require.NoError(t, err)
	require.NoError(t, item.Close(nil))
	// Truncate of a new file marks zeros valid. Simulate a partially cached
	// existing game file: only the five patched bytes are known locally.
	item.mu.Lock()
	item.info.Rs = ranges.Ranges{{Pos: 1024 * 1024, Size: 5}}
	require.NoError(t, item._save())
	item.mu.Unlock()
	require.NoError(t, c.PreserveProtectedDelete(name))
	root := filepath.Join(config.GetCacheDir(), "steam-data-recovery")
	dirs, err := os.ReadDir(root)
	require.NoError(t, err)
	require.Len(t, dirs, 1)
	dir := filepath.Join(root, dirs[0].Name())
	metadata, err := os.ReadFile(filepath.Join(dir, "recovery.json"))
	require.NoError(t, err)
	var record cloudSSDDeletedData
	require.NoError(t, json.Unmarshal(metadata, &record))
	require.Equal(t, name, record.Path)
	require.Equal(t, int64(16*1024*1024), record.Info.Size)
	require.Equal(t, ranges.Ranges{{Pos: 1024 * 1024, Size: 5}}, record.Info.Rs)
	require.Len(t, record.ValidRanges, 1)
	require.Equal(t, fmt.Sprintf("%x", sha256.Sum256([]byte("PATCH"))), record.ValidRanges[0].SHA256)
	c.Remove(name)
	_, err = os.Stat(c.toOSPath(name))
	require.True(t, os.IsNotExist(err))
	saved, err := os.Open(filepath.Join(dir, "data.sparse"))
	require.NoError(t, err)
	defer saved.Close()
	data := make([]byte, 5)
	_, err = saved.ReadAt(data, 1024*1024)
	require.NoError(t, err)
	require.Equal(t, "PATCH", string(data))
	// Nothing implicitly declares the unlisted sparse holes to be valid data.
	require.False(t, record.Info.Rs.Present(ranges.Range{Pos: 0, Size: record.Info.Size}))
}

func TestCloudSSDDeletePreservationRefusesActiveOrUnavailableRecovery(t *testing.T) {
	oldCache := config.GetCacheDir()
	require.NoError(t, config.SetCacheDir(t.TempDir()))
	t.Cleanup(func() { require.NoError(t, config.SetCacheDir(oldCache)) })
	opt := vfscommon.Opt
	opt.CachePollInterval = 0
	opt.HandleCaching = 0
	opt.WriteBack = fs.Duration(time.Hour)
	_, c := newTestCacheOpt(t, opt)
	name := "SteamLibrary/steamapps/downloading/3240220/patch.rpf"
	item := c.Item(name)
	require.NoError(t, item.Open(nil))
	_, err := item.WriteAt([]byte("UNIQUE"), 0)
	require.NoError(t, err)
	require.Error(t, c.PreserveProtectedDelete(name), "open writers cannot be snapshotted consistently")
	require.NoError(t, item.Close(nil))
	// A failed recovery destination must leave the live dirty file untouched.
	root := filepath.Join(config.GetCacheDir(), "steam-data-recovery")
	require.NoError(t, os.WriteFile(root, []byte("blocked"), 0600))
	require.Error(t, c.PreserveProtectedDelete(name))
	data, err := os.ReadFile(c.toOSPath(name))
	require.NoError(t, err)
	require.Equal(t, "UNIQUE", string(data))
	item.mu.Lock()
	require.True(t, item.info.Dirty)
	item.mu.Unlock()
}

func TestCloudSSDDeletePreservationScope(t *testing.T) {
	for _, name := range []string{"steamapps/downloading/12/a", "Library/SteamApps/Downloading/3240220/update/a"} {
		require.True(t, cloudSSDSteamDataPath(name), name)
	}
	for _, name := range []string{"steamapps/common/Game/a", "steamapps/downloading/a", "steamapps/downloading/not-an-app/a", "steamapps/downloading/12", "other/a"} {
		require.False(t, cloudSSDSteamDataPath(name), name)
	}
}
