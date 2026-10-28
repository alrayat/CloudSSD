package vfscache

import (
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"os"
	"path"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"

	"github.com/rclone/rclone/fs/config"
	"github.com/rclone/rclone/lib/ranges"
)

var cloudSSDProgressName = regexp.MustCompile(`^(appmanifest_[0-9]+\.acf(?:\..+)?|state_[0-9]+(?:_[0-9]+)*\.patch)$`)

func cloudSSDProgressPath(name string) bool {
	name = strings.ToLower(strings.ReplaceAll(name, `\`, "/"))
	return strings.Contains("/"+name, "/steamapps/") && cloudSSDProgressName.MatchString(path.Base(name))
}

// Checkpoints are local recovery copies, never shipped or automatically applied
// over Steam's state. Steam still decides which downloaded game bytes are valid.
// Caller holds item.mu and the cache handle is still open.
func (item *Item) cloudSSDCheckpoint() error {
	if item.info.Size <= 0 || item.info.Size > 4*1024*1024 ||
		!item.info.Rs.Present(ranges.Range{Pos: 0, Size: item.info.Size}) {
		return nil
	}
	if err := item.fd.Sync(); err != nil {
		return err
	}
	data := make([]byte, item.info.Size)
	if _, err := item.fd.ReadAt(data, 0); err != nil {
		return err
	}
	identity := sha256.Sum256([]byte(item.name))
	digest := sha256.Sum256(data)
	dir := filepath.Join(config.GetCacheDir(), "steam-progress-journal", fmt.Sprintf("%x", identity))
	if err := os.MkdirAll(dir, 0700); err != nil {
		return err
	}
	final := filepath.Join(dir, fmt.Sprintf("%x.json", digest))
	if _, err := os.Stat(final); err == nil {
		now := time.Now()
		return os.Chtimes(final, now, now)
	}
	metadata, err := json.Marshal(item.info)
	if err != nil {
		return err
	}
	record := struct {
		Path     string
		SHA256   string
		SavedAt  time.Time
		Data     []byte
		Metadata json.RawMessage
	}{item.name, fmt.Sprintf("%x", digest), time.Now().UTC(), data, metadata}
	file, err := os.CreateTemp(dir, ".checkpoint-*")
	if err != nil {
		return err
	}
	tmp := file.Name()
	defer os.Remove(tmp)
	defer file.Close()
	if err = json.NewEncoder(file).Encode(record); err != nil {
		return err
	}
	if err = file.Sync(); err != nil {
		return err
	}
	if err = file.Close(); err != nil {
		return err
	}
	if err = os.Rename(tmp, final); err != nil {
		return err
	}
	// Keep four distinct versions per Steam progress file. Never remove a cache
	// write or a cloud object while trimming these redundant journal copies.
	entries, err := os.ReadDir(dir)
	if err != nil {
		return err
	}
	type saved struct {
		name string
		at   time.Time
	}
	var copies []saved
	for _, e := range entries {
		if !e.Type().IsRegular() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		copies = append(copies, saved{e.Name(), info.ModTime()})
	}
	sort.Slice(copies, func(i, j int) bool { return copies[i].at.After(copies[j].at) })
	for i := 4; i < len(copies); i++ {
		_ = os.Remove(filepath.Join(dir, copies[i].name))
	}
	return nil
}
